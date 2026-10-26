using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Experiments.Oracle;

internal sealed record OracleTruth<TState>(TState TrueCurrentState);

public sealed record OracleEvaluationSummary(string OracleId, MaintenanceActionKind SelectedAction, int FutureInformationAccessCount, bool Deployable);

public sealed class CurrentStateOracleEvaluator<TState>
{
    public OracleEvaluationSummary EvaluateForFixture(TState exactCurrentState, Func<TState, MaintenanceActionKind> rule)
    {
        var truth = new OracleTruth<TState>(exactCurrentState);
        return new("ORC-CURRENT-STATE", rule(truth.TrueCurrentState), 0, false);
    }

    public OracleResultRecord CreateRawRecord() => new();
}
