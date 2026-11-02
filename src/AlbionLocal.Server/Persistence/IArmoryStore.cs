using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

public interface IArmoryStore
{
    IReadOnlyList<ArmorySample> LoadArmorySamples(long since);
    void SaveArmorySamples(IReadOnlyList<ArmorySample> samples, long retainSince);
}
