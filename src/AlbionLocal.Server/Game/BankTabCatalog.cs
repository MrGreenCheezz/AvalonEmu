namespace AlbionLocal.Server.Game;

/// <summary>The fixed personal-tab identities used by client 345319 (bgf.u0, cctor 0x1805C4310).
/// The client chooses the first unused entry for a purchase; tab prices come from CityBank/TabCost.</summary>
public static class BankTabCatalog
{
    public static IReadOnlyList<Guid> Ids { get; } = Array.AsReadOnly<Guid>(
    [
        new("7deb25d2-b3d8-4c7d-a0fa-2778d2eb0bd9"), new("7c482dad-3b55-4fcb-8b3d-225e45adb625"),
        new("55de30b1-0cf3-4fe1-8324-514b845c67df"), new("02f64e9b-0d88-438b-b2bf-ea159fa753a6"),
        new("6b8a3785-e254-4dc5-9a3d-2a6a1143f01e"), new("1c9a260e-9779-4ac7-ad69-c74854eac991"),
        new("013af5d3-8a17-41c7-8efb-800ffb44b6a1"), new("99a19569-0108-40a4-83fd-15fd2741035c"),
        new("a782264b-ce0b-49af-93ef-6b368731feab"), new("0536c642-7e1c-4b4a-a137-36266d22450a"),
        new("77acd770-9979-462e-aa2f-6068dfcc36c9"), new("b1c2b2af-3b7a-40c6-a6c1-865b5c6b8a4a"),
        new("6e1c5155-5f0f-4e0b-a204-e9081189ec63"), new("f2ada5dc-b0ae-4c14-b6de-d3d65af5630e"),
        new("8f4d332a-2084-4157-8692-38274f100c18"), new("00df744a-fa7e-4964-81d3-dca7738c1401"),
        new("3ceb7ed0-3f82-4dae-bd9a-c89ce7a8fb50"), new("9cecbe61-8764-4229-b452-b8f1e6de7b86"),
        new("5433bba2-0fac-4e3a-ac49-d65cac9a099e"), new("ac9a5107-31b4-4b3b-a32a-2788ad5dc4af"),
        new("09d4c3f8-ac14-44ba-88c6-dd844f29cf15"), new("ffbc2bdd-91ff-4b0d-9b66-50f2b4272eee"),
        new("df2ef522-db47-4aa8-9aca-d5d609a8f9b0"), new("b9a8cd6c-ef43-40cf-b1e6-e8c4ab416c88"),
        new("7d9aff9a-3ac9-4f1a-9c0f-95d72fe0858b"), new("c65aa827-6495-4161-b90f-8c15547c8835"),
        new("8f19b5c1-8d6b-4b23-bdcb-9eac7146ca00"), new("d14f9d56-f9a3-4e91-a3a9-e974ccf3e353"),
        new("890e3d46-11f8-4075-8873-0c6a979fb246"), new("48c878e6-d0cf-4678-85d5-6423f10ded28"),
        new("3de9b93e-e22f-41ea-a0d7-69d08d13b3a4"), new("fbe43868-d105-48f0-a46d-925efa0e430f"),
    ]);

    public static BankTab Default(EconomySettings economy) =>
        new(Ids[0], economy.BankDefaultTabName, economy.BankDefaultTabIcon, economy.BankDefaultTabColor);
}
