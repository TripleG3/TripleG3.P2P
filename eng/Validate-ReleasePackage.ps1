[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(
    Get-ChildItem -LiteralPath $resolvedPackageDirectory -File -Filter '*.nupkg' |
        Where-Object Name -eq "TripleG3.P2P.$ExpectedVersion.nupkg"
)

if ($packages.Count -ne 1) {
    throw "Expected exactly one TripleG3.P2P package for version $ExpectedVersion in $resolvedPackageDirectory."
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "TripleG3.P2P-package-validation-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $package = $packages[0]
    $assemblyPath = Join-Path $temporaryDirectory 'TripleG3.P2P.dll'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)

    try {
        $nuspecEntries = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspecEntries.Count -ne 1) {
            throw 'Package must contain exactly one nuspec file.'
        }

        $nuspecReader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
        try {
            [xml]$manifest = $nuspecReader.ReadToEnd()
        }
        finally {
            $nuspecReader.Dispose()
        }

        $metadata = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) {
            throw 'Package nuspec does not contain metadata.'
        }

        $packageId = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
        $packageVersion = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
        $licenseExpression = $metadata.SelectSingleNode("*[local-name()='license']").InnerText

        if ($packageId -ne 'TripleG3.P2P') {
            throw "Unexpected package id '$packageId'."
        }

        if ($packageVersion -ne $ExpectedVersion) {
            throw "Package version '$packageVersion' does not match '$ExpectedVersion'."
        }

        if ($licenseExpression -ne 'GPL-3.0-only') {
            throw "Unexpected package license '$licenseExpression'."
        }

        foreach ($requiredEntry in @('README.md', 'LICENSE', 'lib/net10.0/TripleG3.P2P.dll')) {
            if ($null -eq $archive.GetEntry($requiredEntry)) {
                throw "Package is missing $requiredEntry."
            }
        }

        $assemblyEntry = $archive.GetEntry('lib/net10.0/TripleG3.P2P.dll')
        $assemblyInput = $assemblyEntry.Open()
        try {
            $assemblyOutput = [System.IO.File]::Create($assemblyPath)
            try {
                $assemblyInput.CopyTo($assemblyOutput)
            }
            finally {
                $assemblyOutput.Dispose()
            }
        }
        finally {
            $assemblyInput.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $assembly = [System.Reflection.Assembly]::LoadFile($assemblyPath)
    $informationalVersionAttribute = [System.Reflection.CustomAttributeExtensions]::GetCustomAttribute(
        $assembly,
        [System.Reflection.AssemblyInformationalVersionAttribute])

    if ($null -eq $informationalVersionAttribute) {
        throw 'Package assembly does not define AssemblyInformationalVersionAttribute.'
    }

    if ($informationalVersionAttribute.InformationalVersion -ne $ExpectedVersion) {
        throw "Assembly informational version '$($informationalVersionAttribute.InformationalVersion)' does not match '$ExpectedVersion'."
    }

    $consumerDirectory = Join-Path $temporaryDirectory 'consumer'
    New-Item -ItemType Directory -Path $consumerDirectory | Out-Null

    $packageSource = [System.Security.SecurityElement]::Escape($resolvedPackageDirectory)
    $consumerNuGetConfig = Join-Path $consumerDirectory 'NuGet.Config'
    [System.IO.File]::WriteAllText($consumerNuGetConfig, @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@)

    $consumerProject = Join-Path $consumerDirectory 'PackageConsumer.csproj'
    [System.IO.File]::WriteAllText($consumerProject, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TripleG3.P2P" Version="$ExpectedVersion" />
  </ItemGroup>
</Project>
"@)

    $consumerProgram = Join-Path $consumerDirectory 'Program.cs'
    [System.IO.File]::WriteAllText($consumerProgram, @"
using System.Net;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;

var configuration = new ProtocolConfiguration
{
    LocalPort = 9000,
    OutboundEndPoints = [new IPEndPoint(IPAddress.Loopback, 9001)],
    SerializationProtocol = SerializationProtocol.LengthPrefixed
};

ISerialBus bus = SerialBusFactory.CreateUdp();
bus.SubscribeTo<PackageConsumerMessage>(_ => { });
IHubCatalog catalog = new HubCatalog();
var hub = catalog.CreateChatHub(Guid.NewGuid());
var memberId = Guid.NewGuid();
hub.Join(memberId, "PackageConsumer");
var dispatch = hub.SendMessage(memberId, "ready");
var customDispatch = hub.RouteMessage(memberId, new PackageConsumerEvent(true));
var notifications = catalog.CreateNotificationsHub(Guid.NewGuid());
var device = notifications.RegisterDevice(Guid.NewGuid(), memberId, NotificationPlatform.Android, "en-US");
var notificationDispatch = notifications.Route(
    new NotificationRequest("Ready", "Open the app."),
    NotificationRecipient.ForDevices(device.DeviceId));
var wireDelivery = notificationDispatch.Deliveries[0].ToWireDelivery();
Console.WriteLine($"{configuration.SerializationProtocol}:{bus.IsListening}:{dispatch.Revision}:{customDispatch.Revision}:{wireDelivery.Platform}");

[P2PMessage("PackageConsumerMessage")]
public sealed record PackageConsumerMessage([property: P2PProperty(1)] string Text);

[P2PMessage("PackageConsumerEvent")]
public sealed record PackageConsumerEvent([property: P2PProperty(1)] bool Enabled);
"@)

    Invoke-Dotnet -Arguments @('restore', $consumerProject, '--configfile', $consumerNuGetConfig)
    Invoke-Dotnet -Arguments @('build', $consumerProject, '--configuration', 'Release', '--no-restore', '--warnaserror')

    Write-Host "Validated package $($package.Name) and a clean package consumer."
}
finally {
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
}