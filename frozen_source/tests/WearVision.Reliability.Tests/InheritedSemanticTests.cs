namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class ReliabilitySemanticTests
{
    [TestMethod]
    [DataRow("REL-001", DisplayName = "REL-001")]
    [DataRow("REL-002", DisplayName = "REL-002")]
    [DataRow("REL-003", DisplayName = "REL-003")]
    [DataRow("REL-004", DisplayName = "REL-004")]
    [DataRow("REL-005", DisplayName = "REL-005")]
    [DataRow("REL-006", DisplayName = "REL-006")]
    [DataRow("REL-007", DisplayName = "REL-007")]
    [DataRow("REL-008", DisplayName = "REL-008")]
    [DataRow("REL-009", DisplayName = "REL-009")]
    [DataRow("REL-010", DisplayName = "REL-010")]
    [DataRow("REL-011", DisplayName = "REL-011")]
    [DataRow("REL-012", DisplayName = "REL-012")]
    [DataRow("REL-013", DisplayName = "REL-013")]
    [DataRow("REL-014", DisplayName = "REL-014")]
    [DataRow("REL-015", DisplayName = "REL-015")]
    [DataRow("REL-016", DisplayName = "REL-016")]
    [DataRow("REL-017", DisplayName = "REL-017")]
    [DataRow("REL-018", DisplayName = "REL-018")]
    [DataRow("REL-019", DisplayName = "REL-019")]
    [DataRow("REL-020", DisplayName = "REL-020")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class BeliefSemanticTests
{
    [TestMethod]
    [DataRow("BEL-001", DisplayName = "BEL-001")]
    [DataRow("BEL-002", DisplayName = "BEL-002")]
    [DataRow("BEL-003", DisplayName = "BEL-003")]
    [DataRow("BEL-004", DisplayName = "BEL-004")]
    [DataRow("BEL-005", DisplayName = "BEL-005")]
    [DataRow("BEL-006", DisplayName = "BEL-006")]
    [DataRow("BEL-007", DisplayName = "BEL-007")]
    [DataRow("BEL-008", DisplayName = "BEL-008")]
    [DataRow("BEL-009", DisplayName = "BEL-009")]
    [DataRow("BEL-010", DisplayName = "BEL-010")]
    [DataRow("BEL-011", DisplayName = "BEL-011")]
    [DataRow("BEL-012", DisplayName = "BEL-012")]
    [DataRow("BEL-013", DisplayName = "BEL-013")]
    [DataRow("BEL-014", DisplayName = "BEL-014")]
    [DataRow("BEL-015", DisplayName = "BEL-015")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class FailureEventSemanticTests
{
    [TestMethod]
    [DataRow("FEV-001", DisplayName = "FEV-001")]
    [DataRow("FEV-002", DisplayName = "FEV-002")]
    [DataRow("FEV-003", DisplayName = "FEV-003")]
    [DataRow("FEV-004", DisplayName = "FEV-004")]
    [DataRow("FEV-005", DisplayName = "FEV-005")]
    [DataRow("FEV-006", DisplayName = "FEV-006")]
    [DataRow("FEV-007", DisplayName = "FEV-007")]
    [DataRow("FEV-008", DisplayName = "FEV-008")]
    [DataRow("FEV-009", DisplayName = "FEV-009")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class ActionSemanticTests
{
    [TestMethod]
    [DataRow("ACT-001", DisplayName = "ACT-001")]
    [DataRow("ACT-002", DisplayName = "ACT-002")]
    [DataRow("ACT-003", DisplayName = "ACT-003")]
    [DataRow("ACT-004", DisplayName = "ACT-004")]
    [DataRow("ACT-005", DisplayName = "ACT-005")]
    [DataRow("ACT-006", DisplayName = "ACT-006")]
    [DataRow("ACT-007", DisplayName = "ACT-007")]
    [DataRow("ACT-008", DisplayName = "ACT-008")]
    [DataRow("ACT-009", DisplayName = "ACT-009")]
    [DataRow("ACT-010", DisplayName = "ACT-010")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class ObjectiveSemanticTests
{
    [TestMethod]
    [DataRow("OBJ-001", DisplayName = "OBJ-001")]
    [DataRow("OBJ-002", DisplayName = "OBJ-002")]
    [DataRow("OBJ-003", DisplayName = "OBJ-003")]
    [DataRow("OBJ-004", DisplayName = "OBJ-004")]
    [DataRow("OBJ-005", DisplayName = "OBJ-005")]
    [DataRow("OBJ-006", DisplayName = "OBJ-006")]
    [DataRow("OBJ-007", DisplayName = "OBJ-007")]
    [DataRow("OBJ-008", DisplayName = "OBJ-008")]
    [DataRow("OBJ-009", DisplayName = "OBJ-009")]
    [DataRow("OBJ-010", DisplayName = "OBJ-010")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class DecisionSemanticTests
{
    [TestMethod]
    [DataRow("DEC-001", DisplayName = "DEC-001")]
    [DataRow("DEC-002", DisplayName = "DEC-002")]
    [DataRow("DEC-003", DisplayName = "DEC-003")]
    [DataRow("DEC-004", DisplayName = "DEC-004")]
    [DataRow("DEC-005", DisplayName = "DEC-005")]
    [DataRow("DEC-006", DisplayName = "DEC-006")]
    [DataRow("DEC-007", DisplayName = "DEC-007")]
    [DataRow("DEC-008", DisplayName = "DEC-008")]
    [DataRow("DEC-009", DisplayName = "DEC-009")]
    [DataRow("DEC-010", DisplayName = "DEC-010")]
    [DataRow("DEC-011", DisplayName = "DEC-011")]
    [DataRow("DEC-012", DisplayName = "DEC-012")]
    [DataRow("DEC-013", DisplayName = "DEC-013")]
    [DataRow("DEC-014", DisplayName = "DEC-014")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class ValueOfInformationSemanticTests
{
    [TestMethod]
    [DataRow("VOI-001", DisplayName = "VOI-001")]
    [DataRow("VOI-002", DisplayName = "VOI-002")]
    [DataRow("VOI-003", DisplayName = "VOI-003")]
    [DataRow("VOI-004", DisplayName = "VOI-004")]
    [DataRow("VOI-005", DisplayName = "VOI-005")]
    [DataRow("VOI-006", DisplayName = "VOI-006")]
    [DataRow("VOI-007", DisplayName = "VOI-007")]
    [DataRow("VOI-008", DisplayName = "VOI-008")]
    [DataRow("VOI-009", DisplayName = "VOI-009")]
    [DataRow("VOI-010", DisplayName = "VOI-010")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class RiskMeasureExclusionTests
{
    [TestMethod]
    [DataRow("RSK-001", DisplayName = "RSK-001")]
    [DataRow("RSK-002", DisplayName = "RSK-002")]
    [DataRow("RSK-003", DisplayName = "RSK-003")]
    [DataRow("RSK-004", DisplayName = "RSK-004")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class OracleFirewallSemanticTests
{
    [TestMethod]
    [DataRow("ORA-001", DisplayName = "ORA-001")]
    [DataRow("ORA-002", DisplayName = "ORA-002")]
    [DataRow("ORA-003", DisplayName = "ORA-003")]
    [DataRow("ORA-004", DisplayName = "ORA-004")]
    [DataRow("ORA-005", DisplayName = "ORA-005")]
    [DataRow("ORA-006", DisplayName = "ORA-006")]
    [DataRow("ORA-007", DisplayName = "ORA-007")]
    [DataRow("ORA-008", DisplayName = "ORA-008")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class NumericalSemanticTests
{
    [TestMethod]
    [DataRow("NUM-001", DisplayName = "NUM-001")]
    [DataRow("NUM-002", DisplayName = "NUM-002")]
    [DataRow("NUM-003", DisplayName = "NUM-003")]
    [DataRow("NUM-004", DisplayName = "NUM-004")]
    [DataRow("NUM-005", DisplayName = "NUM-005")]
    [DataRow("NUM-006", DisplayName = "NUM-006")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class ProvenanceSemanticTests
{
    [TestMethod]
    [DataRow("PRO-001", DisplayName = "PRO-001")]
    [DataRow("PRO-002", DisplayName = "PRO-002")]
    [DataRow("PRO-003", DisplayName = "PRO-003")]
    [DataRow("PRO-004", DisplayName = "PRO-004")]
    [DataRow("PRO-005", DisplayName = "PRO-005")]
    [DataRow("PRO-006", DisplayName = "PRO-006")]
    [DataRow("PRO-007", DisplayName = "PRO-007")]
    [DataRow("PRO-008", DisplayName = "PRO-008")]
    [DataRow("PRO-009", DisplayName = "PRO-009")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}

[TestClass]
public sealed class StatisticalSemanticTests
{
    [TestMethod]
    [DataRow("STA-001", DisplayName = "STA-001")]
    [DataRow("STA-002", DisplayName = "STA-002")]
    [DataRow("STA-003", DisplayName = "STA-003")]
    [DataRow("STA-004", DisplayName = "STA-004")]
    [DataRow("STA-005", DisplayName = "STA-005")]
    [DataRow("STA-006", DisplayName = "STA-006")]
    [DataRow("STA-007", DisplayName = "STA-007")]
    public void Run(string testId) => SemanticAssertions.AssertCase(testId);
}
