namespace AlbionLocal.Server.Game;

/// <summary>Personal bank-tab metadata (345319 BankVaultInfo keys 2–5). The containing character and
/// resolved vault location own the tab; list position is its order. Items retain their existing VaultKey.</summary>
public sealed record BankTab(Guid Id, string Name, string Icon, int Color);
