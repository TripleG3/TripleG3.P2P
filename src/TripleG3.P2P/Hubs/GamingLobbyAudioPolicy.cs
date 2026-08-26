namespace TripleG3.P2P.Hubs;

[Flags]
public enum GamingLobbyAudioPolicy
{
    None = 0,
    All = 1,
    Team = 2,
    AllAndTeam = All | Team
}