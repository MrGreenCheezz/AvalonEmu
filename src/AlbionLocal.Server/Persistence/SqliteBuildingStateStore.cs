using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>The owner's settings and the nutrition of the players' stations (schema v51+ building_states; analysis/work/stageR/buildings/SPEC.md).</summary>
public interface IBuildingStateStore
{
    IReadOnlyList<BuildingState> LoadBuildingStates();

    void SaveBuildingState(BuildingState state);

    void DeleteBuildingState(Guid id);
}

/// <summary><see cref="IBuildingStateStore"/> in the character database.</summary>
public sealed partial class SqliteCharacterRepository : IBuildingStateStore
{
    public IReadOnlyList<BuildingState> LoadBuildingStates()
    {
        lock (_sync)
        {
            var list = new List<BuildingState>();
            using var select = Command("SELECT id, friend_fee, user_fee, earned, nutrition, reward_storage, reward_per_nutrition, preset FROM building_states", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new BuildingState(Guid.Parse(r.GetString(0)))
                {
                    FriendFee = r.GetInt64(1), UserFee = r.GetInt64(2), Earned = r.GetInt64(3), Nutrition = r.GetInt64(4),
                    RewardStorage = r.GetInt64(5), RewardPerNutrition = r.GetInt64(6), Preset = r.GetInt32(7),
                });
            return list;
        }
    }

    public void SaveBuildingState(BuildingState s)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO building_states (id, friend_fee, user_fee, earned, nutrition, reward_storage, reward_per_nutrition, preset)
                VALUES ($id, $friend, $user, $earned, $nutrition, $storage, $reward, $preset)
                ON CONFLICT (id) DO UPDATE SET friend_fee = $friend, user_fee = $user, earned = $earned, nutrition = $nutrition,
                    reward_storage = $storage, reward_per_nutrition = $reward, preset = $preset
                """, null, ("$id", s.Id.ToString()), ("$friend", s.FriendFee), ("$user", s.UserFee), ("$earned", s.Earned), ("$nutrition", s.Nutrition),
                ("$storage", s.RewardStorage), ("$reward", s.RewardPerNutrition), ("$preset", s.Preset));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteBuildingState(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM building_states WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }
}
