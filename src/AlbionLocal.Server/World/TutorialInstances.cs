using System.Security.Cryptography;
using System.Text;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server;

/// <summary>Private tutorial identity. Authored cluster ids remain the keys of all game data and saves.</summary>
public static class TutorialInstances
{
    public static bool IsTutorialCluster(string cluster) => cluster is "1008" or "1009" or "1010" or "1011";

    public static bool Owns(Character character, World? world) => world?.Config.TutorialOpening == true
        && world.OpeningTutorial is not null && character.Tutorial is { Version: 1 }
        && IsTutorialCluster(character.Cluster);

    // Keep the original Lighthouse identifier stable; a character's other maps get independent identities.
    public static string Key(string cluster, Guid owner) => "@TUTORIALSINGLE@" +
        (cluster == "1008" ? owner : new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"albionlocal:tutorial:{owner:D}:{cluster}")))).ToString("D");

    public static string ClientCluster(Character character, World? world) =>
        Owns(character, world) ? Key(character.Cluster, character.Id) : character.Cluster;
}
