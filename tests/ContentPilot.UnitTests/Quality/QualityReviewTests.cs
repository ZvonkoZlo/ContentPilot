using ContentPilot.Domain.Quality;
using Shouldly;

namespace ContentPilot.UnitTests.Quality;

/// <summary>
/// The closed-enum contract, asserted rather than documented. Every value has exactly one
/// owning gate, and a gate cannot file a finding it does not own — which is what keeps
/// remediation a reviewable switch statement instead of a second model call.
/// </summary>
public sealed class QualityReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static QualityReview Review(QaGate gate, params QaFinding[] findings) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, gate, QaOutcome.Fail, 0.5, findings, Now);

    [Fact]
    public void A_vision_model_cannot_claim_a_measurement_finding()
    {
        // Text overflow is answered exactly by the layout engine. Letting a model report it
        // would put a guess in front of a measurement on a question with a real answer.
        var finding = new QaFinding { Code = QaFindingCode.TextOverflow, Severity = QaSeverity.Blocking };

        Should.Throw<ArgumentException>(() => Review(QaGate.Visual, finding))
            .Message.ShouldContain("Deterministic");
    }

    [Fact]
    public void The_deterministic_gate_cannot_claim_a_judgement_finding()
    {
        var finding = new QaFinding { Code = QaFindingCode.WeakHook, Severity = QaSeverity.Major };

        Should.Throw<ArgumentException>(() => Review(QaGate.Deterministic, finding));
    }

    [Fact]
    public void Each_gate_accepts_its_own_findings()
    {
        Should.NotThrow(() => Review(QaGate.Visual,
            new QaFinding { Code = QaFindingCode.GarbledText, Severity = QaSeverity.Blocking, Confidence = 0.91 }));

        Should.NotThrow(() => Review(QaGate.Marketing,
            new QaFinding { Code = QaFindingCode.UngroundedClaim, Severity = QaSeverity.Blocking }));
    }

    [Fact]
    public void Every_finding_code_has_exactly_one_owning_gate()
    {
        // The bands are the mapping, so a code added outside them would silently land in
        // the deterministic gate. This is the test that notices.
        foreach (var code in Enum.GetValues<QaFindingCode>())
        {
            var expected = (int)code switch
            {
                >= 100 and < 500 => QaGate.Deterministic,
                >= 500 and < 600 => QaGate.Deterministic,
                >= 600 and < 700 => QaGate.Visual,
                >= 700 and < 800 => QaGate.Marketing,
                _ => throw new InvalidOperationException(
                    $"{code} = {(int)code} sits outside every numbered band; give it one before shipping it."),
            };

            QaFindingCodes.GateFor(code).ShouldBe(expected, code.ToString());
        }
    }

    [Fact]
    public void Finding_codes_are_explicitly_numbered_so_the_wire_form_cannot_drift()
    {
        // Implicit numbering would renumber every later value when one is inserted, and the
        // persisted rows would start meaning something else.
        Enum.GetValues<QaFindingCode>().ShouldAllBe(c => (int)c >= 100);
    }

    [Fact]
    public void A_score_outside_zero_to_one_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new QualityReview(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, QaGate.Deterministic,
                QaOutcome.Pass, 1.4, [], Now));
    }

    [Fact]
    public void Blocking_findings_are_visible_without_reading_the_payload()
    {
        var review = Review(QaGate.Marketing,
            new QaFinding { Code = QaFindingCode.WeakHook, Severity = QaSeverity.Minor },
            new QaFinding { Code = QaFindingCode.UngroundedClaim, Severity = QaSeverity.Blocking });

        review.HasBlockingFindings.ShouldBeTrue();
    }

    [Fact]
    public void A_passing_gate_is_still_recorded()
    {
        // The QA pass rate is the metric that catches a miscalibrated prompt. It cannot be
        // computed if a clean gate leaves no row.
        var review = new QualityReview(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 1, QaGate.Deterministic, QaOutcome.Pass, 1.0, [], Now);

        review.Outcome.ShouldBe(QaOutcome.Pass);
        review.Findings.ShouldBeEmpty();
        review.HasBlockingFindings.ShouldBeFalse();
    }
}
