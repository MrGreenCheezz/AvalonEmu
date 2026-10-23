using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The unhandled-ops audit (analysis/work/stageH/ops-audit/SPEC.md with its verification log): every operation it lists
/// is answered by a handler or content/opcode-stubs.json, so none takes the unhandled path (and its warning) any more;
/// the layouts of the small answers in code (§2, §3) and the "ignore" stub kind.
/// </summary>
public static class OpsAuditTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("ops audit: every listed op answered by a handler or a stub (none unhandled); ignore = no answer, refused with a response class", Classified),
        ("ops audit: SubscribeToCluster -> one ClusterInfoUpdate per known id (outposts 3/3/6, none: 27/28 absent, no 44-47), none for [], unknown or territory ids", ClusterInfo),
        ("ops audit: TravelIslandShowroom RC 1 bare, event 377 empty, campaign month 24309, GvG rank -1, journal tracker 600 (suggested, echoed, kept over a relog)", SmallAnswers),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>The operations the audit names (§1, §6), each answered in code or from the stub file now.</summary>
    private static readonly OperationCodes[] Listed =
    [
        OperationCodes.SubscribeToCluster, OperationCodes.UseFunction, OperationCodes.TravelIslandShowroom, OperationCodes.QuickSellAuctionQueryAction,
        OperationCodes.GetPersonalSeasonPastRewardData, OperationCodes.GetFactionWarfareCampaignData, OperationCodes.EventBasedPopupAddSeen,
        OperationCodes.TrackAlbionJournalAchievements, OperationCodes.ExtendedHardwareStats, OperationCodes.ClientPerformanceStats,
        OperationCodes.ClientFrametimeSpike, OperationCodes.ProductShopUserEvent, OperationCodes.PartyFinderRegisterForUpdates,
        OperationCodes.PartyFinderUnregisterForUpdates, OperationCodes.PartyFinderRequestEquipmentSnapshot, OperationCodes.BoostFarmable,
        OperationCodes.RequestEstimatedMarketValue, OperationCodes.SetAchievementsAutoLearn, OperationCodes.GetGvgSeasonRank,
        OperationCodes.GetCharacterSteamAchievements, OperationCodes.ClaimAlbionJournalReward, OperationCodes.ReadMail,
        OperationCodes.GetPvpChallengeData, OperationCodes.GetShopTilesForCategory, OperationCodes.GetMyTerritories,
        OperationCodes.GetGuildEnergyDrainInfo, OperationCodes.GetGvgSeasonRankings, OperationCodes.GetGuildAccountLogs,
        OperationCodes.QueryGuildPlayerStats, OperationCodes.GetGuildChallengePoints, OperationCodes.GetGvgSeasonContributionByActivity,
        OperationCodes.GetGuildMightCategoryOverview, OperationCodes.GvGSeasonPlayerGuildParticipationDetails,
    ];

    /// <summary>content/opcode-stubs.json as ServerHost loads it.</summary>
    private static OpcodeStubs Stubs()
    {
        OpcodeTable.Build(typeof(LoginSession), typeof(WorldSession), typeof(ChatSession));
        return OpcodeStubs.Load(Path.Combine(DataDir(), "..", "content", "opcode-stubs.json"), code =>
            (OpcodeTable.For(typeof(LoginSession)).Find(code) ?? OpcodeTable.For(typeof(WorldSession)).Find(code) ?? OpcodeTable.For(typeof(ChatSession)).Find(code))?.Method);
    }

    /// <summary>A joined player of a Thetford <see cref="EconomyTest.Town"/> whose session has the content stubs.</summary>
    private static (WorldSession Session, FakePeer Peer) Join(EconomyTest.Town town, Guid id, OpcodeStubs stubs)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, town.World) { Stubs = stubs };
        peer.Disconnected = session.OnDisconnected;
        lock (town.World.Sync) session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
        Check(peer.Responses is [{ ReturnCode: 0 }], "joined");
        return (session, peer);
    }

    private static OperationRequest Bare(OperationCodes code, int? id) => new(AlbionSerializer.PhotonOperationCode,
        id is { } rid ? new() { [ReservedKeys.OperationCode] = (short)code, [ReservedKeys.RequestId] = rid } : new() { [ReservedKeys.OperationCode] = (short)code });

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static void Classified()
    {
        var stubs = Stubs();
        var table = OpcodeTable.For(typeof(WorldSession));
        foreach (var code in Listed)
            Check(table.Find(code) is not null ^ stubs.KindOf(code) is not null, $"{code}: a handler or a stub, not both");
        Check(Listed.Where(c => !AlbionSerializer.ResponseTypes.ContainsKey(c) && table.Find(c) is null).All(c => stubs.KindOf(c) == OpcodeStubs.StubKind.Ignore),
              "the listed ops without a response class and a handler are \"ignore\"");

        using var town = new EconomyTest.Town();
        var c = StarterKit.Default.NewCharacter("Auditor");
        Check(town.Characters.Create(c), "character created");
        var (session, peer) = Join(town, c.Id, stubs);
        var before = Listed.ToDictionary(o => o, o => table.Unhandled.GetValueOrDefault(o));
        var (responses, events) = (peer.Responses.Count, peer.Events.Count);
        foreach (var code in Listed) lock (town.World.Sync) session.OnRequest(Bare(code, 50), encrypted: false);
        Check(Listed.All(o => table.Unhandled.GetValueOrDefault(o) == before[o]), "none of them took the unhandled path (its warning)");
        // Ignore: nothing at all, as before, but counted as stubbed.
        var ignored = Listed.Where(o => stubs.KindOf(o) == OpcodeStubs.StubKind.Ignore).ToList();
        Check(ignored.Count == 9 && ignored.All(o => table.Stubbed.GetValueOrDefault(o) > 0),
              "nine ignore stubs; UseFunction now has the opening-tutorial handler, counted as handled");
        var answered = peer.Responses.Skip(responses).Select(r => (OperationCodes)Convert.ToInt16(r.Parameters[ReservedKeys.OperationCode])).ToHashSet();
        Check(!answered.Overlaps(ignored) && !answered.Contains(OperationCodes.SubscribeToCluster) && !answered.Contains(OperationCodes.TrackAlbionJournalAchievements)
              && !answered.Contains(OperationCodes.GetPersonalSeasonPastRewardData) && !answered.Contains(OperationCodes.UseFunction),
              "no OperationResponse to an op without a response class (but the island showroom's RC)");

        void Refused(string text, string words)
        {
            try
            {
                OpcodeStubs.Parse(text, "test.json", _ => null);
                Check(false, $"accepted: {text}");
            }
            catch (InvalidDataException e)
            {
                Check(e.Message.Contains(words), $"{text}: the error says {words} (got {e.Message})");
            }
        }
        Refused("""{ "ChangeUseFlag": "ignore" }""", "ChangeUseFlag has the response class ChangeUseFlagResponse: \"silent\" for no answer");
        Refused("""{ "AcceptPopups": "silent" }""", "AcceptPopups has no response class");
        Check(OpcodeStubs.Parse("""{ "AcceptPopups": "ignore" }""", "test.json", _ => null).KindOf(OperationCodes.AcceptPopups) == OpcodeStubs.StubKind.Ignore,
              "\"ignore\" for an op without a response class loads");
    }

    private static void ClusterInfo()
    {
        var stubs = Stubs();
        using var town = new EconomyTest.Town();
        var info = Client.Clusters!.World;
        var c = StarterKit.Default.NewCharacter("Mapper");
        Check(town.Characters.Create(c), "character created");
        var (session, peer) = Join(town, c.Id, stubs);
        List<EventData> Ask(params string[] ids)
        {
            var (responses, events) = (peer.Responses.Count, peer.Events.Count);
            lock (town.World.Sync) session.OnRequest(Request(new SubscribeToClusterRequest { agl = ids }, 7), encrypted: false);
            Check(peer.Responses.Count == responses, $"SubscribeToCluster [{string.Join(", ", ids)}]: no response");
            return [.. peer.Events.Skip(events)];
        }
        var two = Ask("0000", "4220");
        Check(two.Count == 2 && two.All(e => Code(e) == (int)EventCodes.ClusterInfoUpdate)
              && two[0].Parameters[0] is "0000" && two[1].Parameters[0] is "4220", "[0000, 4220] → two events 139 with key 0 the ids");
        Check(Ask().Count == 0, "[] (the map closed) → no event");
        var territory = info.Clusters.First(x => x.Territories > 0).Id;
        Check(Ask("ZZZZ", territory).Count == 0, $"an unknown id and a territory cluster ({territory}) → no event");

        var withOutposts = info.Clusters.Where(x => x.Territories == 0 && x.Outposts.Count > 0).Select(x => x.Id).Take(20);
        foreach (var ev in Ask([.. two.Select(e => (string)e.Parameters[0]!), "0004", .. withOutposts]))
        {
            var p = ev.Parameters;
            var id = (string)p[0]!;
            var e = new ClusterInfoUpdateEvent();
            AlbionSerializer.FillFields(e, p);
            var n = info.Find(id)!.Outposts.Count;
            // stageN static dungeons: 68 (the values) only where a static dungeon has an entrance; 65-67 its running events, parallel
            var entrances = info.Find(id)!.Exits.Any(x => x.Attrs.TryGetValue("staticdungeonid", out var sd) && Guid.TryParse(sd, out var g) && g != Guid.Empty);
            Check(!p.ContainsKey(44) && !p.ContainsKey(45) && !p.ContainsKey(46) && !p.ContainsKey(47) && p.ContainsKey(68) == entrances
                  && Enumerable.Range(4, 15).All(k => !p.ContainsKey((byte)k)) && !p.ContainsKey(1)
                  && e.bys?.Length == n && (n == 0 ? !p.ContainsKey(27) && !p.ContainsKey(28) : e.by0?.Length == n && e.by1?.Length == 2 * n)
                  && e.bzo?.Length == 0 && e.bzp?.Length == 0 && e.bzq?.Length == 0 && e.bzr?.Length == 0 && e.bzs?.Length == 0 && e.bzt?.Length == 0
                  && e.bz2?.Length == e.bz3?.Length && e.bz2?.Length == e.bz4?.Length && (entrances || e.bz2?.Length == 0) && e.byt?.Length == 0 && e.byu?.Length == 0
                  && e is { byb: "", byc: "", bzv: "", by7: -1, bzu: -1 },
                  $"{id}: no cluster value (44-47), no territory keys, 19/27/28 sized by its {n} outpost(s), empty treasure and hideout arrays, static dungeon keys only with entrances, 34/57 = -1");
        }
        Check(WorldSession.ClusterInfo(info, "4220") is { bys.Length: 3, by0.Length: 3, by1.Length: 6 } && WorldSession.ClusterInfo(info, "0004") is { by0: null, by1: null, bys.Length: 0 },
              "4220: 19/27/28 = 3/3/6; 0004: 27 and 28 absent (as official)");
    }

    private static void SmallAnswers()
    {
        var stubs = Stubs();
        var journal = Client.Clusters!.Journal;
        using var town = new EconomyTest.Town();
        var c = StarterKit.Default.NewCharacter("Tracker");
        Check(town.Characters.Create(c), "character created");
        var (session, peer) = Join(town, c.Id, stubs);
        (List<OperationResponse> Responses, List<EventData> Events) Send(OperationRequest request)
        {
            var (responses, events) = (peer.Responses.Count, peer.Events.Count);
            lock (town.World.Sync) session.OnRequest(request, encrypted: false);
            return ([.. peer.Responses.Skip(responses)], [.. peer.Events.Skip(events)]);
        }

        Check(Send(Request(new TravelIslandShowroomRequest { aha = 5 }, 1)) is { Responses: [{ ReturnCode: WorldSession.NoShowroom } island], Events: [] }
              && island.Parameters.Keys.Order().SequenceEqual(new[] { ReservedKeys.OperationCode, ReservedKeys.RequestId }) && island.Parameters[ReservedKeys.RequestId] is 1,
              "TravelIslandShowroom: one response, RC 1, key 255 echoed, nothing but 253/255");
        Check(Send(Bare(OperationCodes.GetPersonalSeasonPastRewardData, 2)) is { Responses: [], Events: [var past] } && Code(past) == (int)EventCodes.PersonalSeasonPastSeasonDataEvent
              && Enumerable.Range(2, 7).All(k => past.Parameters[(byte)k] is Array { Length: 0 }),
              "GetPersonalSeasonPastRewardData: no response, one event 377 with keys 2-8 empty (no past seasons)");
        Check(WorldSession.CampaignMonth(new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc)) == 24309
              && WorldSession.CampaignMonth(new DateTime(2026, 10, 16, 23, 0, 0, DateTimeKind.Utc)) == 24309, "campaign month on 2026-10-16/17: 24309 (official)");
        var campaign = Send(Bare(OperationCodes.GetFactionWarfareCampaignData, 3)).Responses.Single();
        Check(campaign is { ReturnCode: 0 } && Convert.ToInt32(campaign.Parameters[4]) == WorldSession.CampaignMonth(DateTime.UtcNow)
              && campaign.Parameters[5] is long[] { Length: 6 } && campaign.Parameters[6] is int[] { Length: 6 } && campaign.Parameters[8] is int[] { Length: 6 },
              "GetFactionWarfareCampaignData: key 4 this month, keys 5-8 six zeros");
        Check(Send(Bare(OperationCodes.GetGvgSeasonRank, 4)).Responses is [{ ReturnCode: 0 } rank] && rank.Parameters[0] is -1, "GetGvgSeasonRank: {0: -1}, no rank");

        // The tracker: [] at the Join → the suggestions; a pinned list is echoed and kept, also over a relog.
        string[] Track(WorldSession s, FakePeer p, params string[] names)
        {
            var events = p.Events.Count;
            lock (town.World.Sync) s.OnRequest(Request(new TrackAlbionJournalAchievementsRequest { ag3 = names }, null), encrypted: false);
            Check(p.Events.Skip(events).ToList() is [var ev] && Code(ev) == (int)EventCodes.JournalAchievementProgressUpdate && !ev.Parameters.ContainsKey(0)
                  && ev.Parameters[2] is long[] progress && ev.Parameters[1] is string[] list && progress.Length == list.Length && progress.All(x => x == 0),
                  $"TrackAlbionJournalAchievements [{names.Length}]: one event 600, key 0 = 0 (full list), key 2 parallel zeros");
            return (string[])p.Events[^1].Parameters[1]!;
        }
        var suggested = Track(session, peer);
        Check(suggested.Length == WorldSession.SuggestedJournalCount && suggested.All(journal.Contains) && suggested.Distinct().Count() == suggested.Length
              && journal.Entries.First(e => e.Name == suggested[0]).Category == "TUTORIAL" && journal.Entries.First(e => e.Name == suggested[1]).Category == "GATHERING"
              && journal.Entries.First(e => e.Name == suggested[2]).Category == "PVE" && suggested.All(n => !journal.Entries.First(e => e.Name == n).Steam),
              $"[] at the Join: four journal achievements, TUTORIAL / GATHERING / PVE in turn, no Steam mirror (got {string.Join(", ", suggested)})");
        string[] pinned = [suggested[1], suggested[2]];
        Check(Track(session, peer, pinned).SequenceEqual(pinned), "a pinned list is echoed");
        Check(Track(session, peer).Length == 0, "a later [] (all unpinned) stays empty");
        Check(Track(session, peer, pinned).SequenceEqual(pinned), "pinned again");
        lock (town.World.Sync) peer.Disconnect();
        town.World.Saves.Flush();
        Check(town.Characters.Find(c.Id)?.JournalTracked is { } saved && saved.SequenceEqual(pinned), "the list is saved with the character");
        var (again, againPeer) = Join(town, c.Id, stubs);
        Check(Track(again, againPeer).SequenceEqual(pinned), "after a relog the Join's [] gets the saved list back");
    }
}
