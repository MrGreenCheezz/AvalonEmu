using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.UseFunction, Status = SessionStatus.InWorld)]
    private AlbionResponse? UseTutorialFunction(UseFunctionRequest req)
    {
        if (Player is { } p) Systems.OpeningTutorial.FunctionUsed(p, req.aia);
        return null;
    }
    [Handles(OperationCodes.QuestGiverRequest, Status = SessionStatus.InWorld)]
    private AlbionResponse QuestGiver(QuestGiverRequestRequest req) => new GenericResponse
    {
        ReturnCode = Player is { } p ? Systems.OpeningTutorial.Request(p, req.yz, req.y0, req.y1, req.y2) : (short)1,
    };

    [Handles(OperationCodes.SetNextTutorialState, Status = SessionStatus.InWorld)]
    private AlbionResponse NextTutorial(SetNextTutorialStateRequest req)
    {
        // The timestamp-only request does not prove any mission condition. Movement/dialogue drives progress.
        if (Player is { } p && Systems.OpeningTutorial.Active(p))
            SendEvent(new TutorialUpdateEvent { d18 = p.Character.Tutorial!.TutorialState });
        return new GenericResponse();
    }
}
