#!/usr/bin/env python3
"""Frozen ER-07B-R2F T6 analysis (pre-outcome implementation).

This file is created and fixture-tested before authorization consumption. It
implements only the ER-07A-declared primary family and requested descriptive
reliability/action/ablation analyses. The independent unit is the component
episode/trajectory. Negative paired loss differences favor P-R4.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import os
import statistics
import struct
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence


SCHEMA_VERSION = "er07b-r2f-frozen-analysis-v1"
EVIDENCE_CLASS = "T6_SCIENTIFIC_LOCKED_EXPERIMENT"
PRIMARY_ROW = "T6-S0-PRIMARY"
PRIMARY_CONTRASTS = ("C-P-B7", "C-P-B4", "C-P-B1")
PRIMARY_BASELINES = {"C-P-B7": "B7", "C-P-B4": "B4", "C-P-B1": "B1"}
ROW_ORDER = (
    "T6-S0-PRIMARY",
    "T6-S1-SEV050",
    "T6-S2-SEV075",
    "T6-ABL1",
    "T6-ABL2",
    "T6-ABL5",
    "T6-ABL6",
    "T6-ABL8",
)
EXPECTED_EPISODES = {
    "T6-S0-PRIMARY": 704,
    "T6-S1-SEV050": 256,
    "T6-S2-SEV075": 256,
    "T6-ABL1": 256,
    "T6-ABL2": 256,
    "T6-ABL5": 256,
    "T6-ABL6": 256,
    "T6-ABL8": 256,
}
ABLATION_VARIANTS = {
    "T6-ABL1": "ABL-1",
    "T6-ABL2": "ABL-2",
    "T6-ABL5": "ABL-5",
    "T6-ABL6": "ABL-6",
    "T6-ABL8": "ABL-8",
}
ALPHA = 0.05
BOOTSTRAP_REPLICATES = 9999
DELTA_STAR = 0.03
EPSILON = 0.125
MASTER_SEED = 13_691_961_035_017_441_233
T6_SOURCE_SHA256 = "9171A598E6B441041463A88ABB39CCB6705EE86265B43D601E07F4CABDEC5621"
T6_EXECUTABLE_SHA256 = "4E1F7F2ACC4768E6834B0810C7055362C0B30D525D85720E59792CE4FB0990A6"
T6_CONFIG_SHA256 = "A565B489E3A09F903A80F3A4E466F5331A4873E5C6B1FA179CD6AD1373E28598"
T6_AUTHORIZATION_SHA256 = "140B9016C6B314FFA6B6FE480B46DA45AF09752301F2F4D2A6F58A6494CF299C"
T6_AUTHORIZATION_ID = "ER07B-R1-1152-T6-20260821-EIGHT-ROWS-RUNTIME-EXACT-HASH-READY"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def read_jsonl(path: Path) -> list[dict]:
    records: list[dict] = []
    with path.open("r", encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            value = json.loads(line)
            if not isinstance(value, dict):
                raise ValueError(f"JSON object required: {path}:{line_number}")
            records.append(value)
    return records


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def write_csv_exclusive(path: Path, fieldnames: Sequence[str], rows: Iterable[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        for row in rows:
            writer.writerow(row)


def write_json_exclusive(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        stream.write("\n")


def parse_bool(value: str | bool) -> bool:
    if isinstance(value, bool):
        return value
    token = str(value).strip().lower()
    if token == "true":
        return True
    if token == "false":
        return False
    raise ValueError(f"Boolean token required, observed {value!r}")


def mean(values: Sequence[float]) -> float:
    if not values:
        raise ValueError("Mean requires at least one value")
    return math.fsum(values) / len(values)


def robust_scale_mad(values: Sequence[float]) -> float:
    center = statistics.median(values)
    return 1.4826 * statistics.median(abs(value - center) for value in values)


def beta_continued_fraction(a: float, b: float, x: float) -> float:
    # Modified Lentz algorithm, frozen constants from the standard incomplete-beta formulation.
    max_iterations = 400
    epsilon = 3.0e-14
    floor = 1.0e-300
    qab = a + b
    qap = a + 1.0
    qam = a - 1.0
    c = 1.0
    d = 1.0 - qab * x / qap
    if abs(d) < floor:
        d = floor
    d = 1.0 / d
    h = d
    for m in range(1, max_iterations + 1):
        m2 = 2 * m
        aa = m * (b - m) * x / ((qam + m2) * (a + m2))
        d = 1.0 + aa * d
        if abs(d) < floor:
            d = floor
        c = 1.0 + aa / c
        if abs(c) < floor:
            c = floor
        d = 1.0 / d
        h *= d * c
        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2))
        d = 1.0 + aa * d
        if abs(d) < floor:
            d = floor
        c = 1.0 + aa / c
        if abs(c) < floor:
            c = floor
        d = 1.0 / d
        delta = d * c
        h *= delta
        if abs(delta - 1.0) <= epsilon:
            return h
    raise ArithmeticError("Incomplete-beta continued fraction did not converge")


def regularized_incomplete_beta(x: float, a: float, b: float) -> float:
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    log_bt = math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b) + a * math.log(x) + b * math.log1p(-x)
    bt = math.exp(log_bt)
    if x < (a + 1.0) / (a + b + 2.0):
        return bt * beta_continued_fraction(a, b, x) / a
    return 1.0 - bt * beta_continued_fraction(b, a, 1.0 - x) / b


def paired_t_two_sided(values: Sequence[float]) -> tuple[float, int, float]:
    n = len(values)
    if n < 2:
        raise ValueError("Paired t test requires at least two complete pairs")
    center = mean(values)
    sd = statistics.stdev(values)
    if sd == 0.0:
        return (0.0 if center == 0.0 else math.copysign(math.inf, center), n - 1, 1.0 if center == 0.0 else 0.0)
    statistic = center / (sd / math.sqrt(n))
    x = (n - 1) / ((n - 1) + statistic * statistic)
    p_value = regularized_incomplete_beta(x, 0.5 * (n - 1), 0.5)
    return statistic, n - 1, min(1.0, max(0.0, p_value))


def holm_adjust(p_values: dict[str, float]) -> tuple[dict[str, float], dict[str, bool]]:
    ordered = sorted(p_values.items(), key=lambda item: (item[1], item[0]))
    adjusted: dict[str, float] = {}
    running = 0.0
    m = len(ordered)
    continue_rejecting = True
    rejected = {key: False for key in p_values}
    for index, (key, p_value) in enumerate(ordered):
        running = max(running, (m - index) * p_value)
        adjusted[key] = min(1.0, running)
        threshold = ALPHA / (m - index)
        if continue_rejecting and p_value <= threshold:
            rejected[key] = True
        else:
            continue_rejecting = False
    return adjusted, rejected


MASK64 = (1 << 64) - 1


class Xoshiro256StarStar:
    def __init__(self, digest: bytes) -> None:
        if len(digest) != 32:
            raise ValueError("A SHA-256 digest is required")
        self.state = list(struct.unpack("<QQQQ", digest))
        if not any(self.state):
            raise ValueError("All-zero xoshiro state is prohibited for this analysis")

    @staticmethod
    def _rotl(value: int, count: int) -> int:
        return ((value << count) & MASK64) | (value >> (64 - count))

    def next_u64(self) -> int:
        s0, s1, s2, s3 = self.state
        result = (self._rotl((s1 * 5) & MASK64, 7) * 9) & MASK64
        t = (s1 << 17) & MASK64
        s2 ^= s0
        s3 ^= s1
        s1 ^= s2
        s0 ^= s3
        s2 ^= t
        s3 = self._rotl(s3, 45)
        self.state = [s0 & MASK64, s1 & MASK64, s2 & MASK64, s3 & MASK64]
        return result


def rng_digest(context_id: str, replicate: int) -> bytes:
    # Exact encoding mirrors Sha256XoshiroRandomStreamFactory with StreamPurpose.BootstrapResample=10.
    values = (
        b"rng-v1",
        "ER07B-R2F-FROZEN-ANALYSIS".encode(),
        PRIMARY_ROW.encode(),
        b"PRIMARY-PAIRED-TRAJECTORIES",
        context_id.encode(),
        b"PRIMARY-ANALYSIS",
        b"BOOTSTRAP-CLUSTER-PAIRING",
    )
    payload = bytearray()
    payload += struct.pack("<I", len(values[0])) + values[0]
    payload += struct.pack("<Q", MASTER_SEED)
    for value in values[1:]:
        payload += struct.pack("<I", len(value)) + value
    payload += struct.pack("<i", 10)
    payload += struct.pack("<Q", replicate)
    return hashlib.sha256(payload).digest()


def bootstrap_means(values: Sequence[float], context_id: str, replicates: int = BOOTSTRAP_REPLICATES) -> list[float]:
    ordered = list(values)
    n = len(ordered)
    if n < 2:
        raise ValueError("Bootstrap requires at least two clusters")
    output: list[float] = []
    for replicate in range(1, replicates + 1):
        stream = Xoshiro256StarStar(rng_digest(context_id, replicate))
        total = math.fsum(ordered[stream.next_u64() % n] for _ in range(n))
        output.append(total / n)
    return output


def quantile_linear(sorted_values: Sequence[float], probability: float) -> float:
    if not 0.0 <= probability <= 1.0 or not sorted_values:
        raise ValueError("Invalid quantile request")
    position = (len(sorted_values) - 1) * probability
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return sorted_values[lower]
    fraction = position - lower
    return sorted_values[lower] * (1.0 - fraction) + sorted_values[upper] * fraction


def percentile_interval(values: Sequence[float], context_id: str) -> tuple[float, float]:
    samples = sorted(bootstrap_means(values, context_id))
    return quantile_linear(samples, 0.025), quantile_linear(samples, 0.975)


def wilson_interval(successes: int, total: int, z: float = 1.959963984540054) -> tuple[float, float]:
    if total <= 0 or not 0 <= successes <= total:
        raise ValueError("Invalid Wilson counts")
    proportion = successes / total
    denominator = 1.0 + z * z / total
    center = (proportion + z * z / (2.0 * total)) / denominator
    half = z / denominator * math.sqrt(proportion * (1.0 - proportion) / total + z * z / (4.0 * total * total))
    return max(0.0, center - half), min(1.0, center + half)


def delta_relation(value: float) -> str:
    if value <= -DELTA_STAR:
        return "FAVOURS_P-R4_AT_LEAST_DELTA_STAR"
    if value < 0.0:
        return "FAVOURS_P-R4_BELOW_DELTA_STAR"
    if value == 0.0:
        return "EQUAL_TO_ZERO"
    return "FAVOURS_BASELINE"


@dataclass(frozen=True)
class PrimaryResult:
    contrast_id: str
    baseline_id: str
    n: int
    mean_difference: float
    median_difference: float
    sd_difference: float
    mad_scale: float
    ci_low: float
    ci_high: float
    t_statistic: float
    df: int
    raw_p: float


def validate_completion(run_root: Path) -> dict:
    completion_path = run_root / "completion-manifest.json"
    completion = json.loads(completion_path.read_text(encoding="utf-8"))
    if not completion.get("Completed"):
        raise ValueError("T6 completion manifest does not state Completed=true")
    if completion.get("ExecutedRows") != list(ROW_ORDER):
        raise ValueError("Executed-row order/set mismatch")
    if completion.get("AccountedEpisodes") != sum(EXPECTED_EPISODES.values()):
        raise ValueError("Episode accounting mismatch")
    if not completion.get("ScientificEligibility") or completion.get("ManuscriptEligibility"):
        raise ValueError("Unexpected completion scientific/manuscript eligibility flags")
    for member in completion.get("Files", []):
        path = run_root / Path(member["RelativePath"])
        if not path.is_file() or path.stat().st_size != member["SizeBytes"] or sha256_file(path) != member["Sha256"]:
            raise ValueError(f"Completion member mismatch: {member['RelativePath']}")
    return completion


def analyze_primary(run_root: Path) -> tuple[list[PrimaryResult], list[dict], dict[str, list[dict]]]:
    records = read_jsonl(run_root / "statistics" / "paired-contrast-inputs.jsonl")
    grouped: dict[str, list[dict]] = defaultdict(list)
    for record in records:
        if record.get("SchemaVersion") != "t6-paired-contrast-input-v1":
            continue
        if record.get("RowId") == PRIMARY_ROW and record.get("ContrastId") in PRIMARY_CONTRASTS:
            grouped[record["ContrastId"]].append(record)
    results: list[PrimaryResult] = []
    influence_rows: list[dict] = []
    for contrast_id in PRIMARY_CONTRASTS:
        group = sorted(grouped[contrast_id], key=lambda item: item["ComponentEpisodeId"])
        if len(group) != EXPECTED_EPISODES[PRIMARY_ROW]:
            raise ValueError(f"Primary pair count mismatch for {contrast_id}: {len(group)}")
        episode_ids = [item["ComponentEpisodeId"] for item in group]
        if len(set(episode_ids)) != len(episode_ids):
            raise ValueError(f"Duplicate primary episode for {contrast_id}")
        values: list[float] = []
        for item in group:
            proposed = float(item["ProposedLoss"])
            baseline = float(item["BaselineLoss"])
            difference = float(item["Difference"])
            if not (0.0 <= proposed <= 1.0 and 0.0 <= baseline <= 1.0):
                raise ValueError("Loss outside frozen bounded [0,1] scale")
            if abs((proposed - baseline) - difference) > 1.0e-12:
                raise ValueError("Stored paired difference does not reproduce losses")
            values.append(difference)
        center = mean(values)
        median = statistics.median(values)
        sd = statistics.stdev(values)
        mad = robust_scale_mad(values)
        ci_low, ci_high = percentile_interval(values, contrast_id)
        t_stat, df, raw_p = paired_t_two_sided(values)
        results.append(PrimaryResult(contrast_id, PRIMARY_BASELINES[contrast_id], len(values), center, median, sd, mad, ci_low, ci_high, t_stat, df, raw_p))
        ranked = sorted(
            ((abs((center - value) / (len(values) - 1)), episode_id, value) for episode_id, value in zip(episode_ids, values)),
            key=lambda item: (-item[0], item[1]),
        )
        for rank, (absolute_shift, episode_id, value) in enumerate(ranked, 1):
            loo_mean = (len(values) * center - value) / (len(values) - 1)
            influence_rows.append({
                "evidence_class": EVIDENCE_CLASS,
                "contrast_id": contrast_id,
                "influence_rank": rank,
                "component_episode_id": episode_id,
                "difference": format(value, ".17g"),
                "full_mean": format(center, ".17g"),
                "leave_one_out_mean": format(loo_mean, ".17g"),
                "absolute_mean_shift": format(absolute_shift, ".17g"),
                "mean_sign_changed": str((center < 0.0) != (loo_mean < 0.0)).upper(),
                "delta_relation_changed": str(delta_relation(center) != delta_relation(loo_mean)).upper(),
            })
    return results, influence_rows, grouped


def first_decisions(path: Path) -> dict[str, dict[str, str]]:
    rows = read_csv(path)
    output: dict[str, dict[str, str]] = {}
    for row in rows:
        episode = row["component_episode_id"]
        exposure = float(row["decision_exposure"])
        current = output.get(episode)
        if current is None or exposure < float(current["decision_exposure"]):
            output[episode] = row
    return output


def selected_decision_path(run_root: Path, row_id: str) -> Path:
    variant = "main" if not row_id.startswith("T6-ABL") else ABLATION_VARIANTS[row_id]
    return run_root / "raw" / f"{row_id}--{variant}" / "SCH-DEC.csv"


def safe_log_score(probability: float, observed: int) -> float:
    if observed == 1:
        return math.inf if probability <= 0.0 else -math.log(probability)
    return math.inf if probability >= 1.0 else -math.log1p(-probability)


def reliability_action_rows(run_root: Path) -> tuple[list[dict], dict, dict[str, dict]]:
    calibration = read_jsonl(run_root / "statistics" / "calibration-reliability-inputs.jsonl")
    actions = read_jsonl(run_root / "actions" / "action-records.jsonl")
    calibration_by_row: dict[str, list[dict]] = defaultdict(list)
    actions_by_row: dict[str, list[dict]] = defaultdict(list)
    for record in calibration:
        calibration_by_row[record["RowId"]].append(record)
    for record in actions:
        actions_by_row[record["RowId"]].append(record)
    register: list[dict] = []
    row_summaries: dict[str, dict] = {}
    for row_id in ROW_ORDER:
        cal = sorted(calibration_by_row[row_id], key=lambda item: item["ComponentEpisodeId"])
        acts = sorted(actions_by_row[row_id], key=lambda item: item["ComponentEpisodeId"])
        expected = EXPECTED_EPISODES[row_id]
        if len(cal) != expected or len(acts) != expected:
            raise ValueError(f"Reliability/action count mismatch for {row_id}")
        if len({item["ComponentEpisodeId"] for item in cal}) != expected:
            raise ValueError(f"Duplicate calibration episode for {row_id}")
        incomplete = sum(not bool(item["Complete"]) for item in cal)
        failures = sum(bool(item["ObservedFirstPassage"]) for item in cal)
        violations = sum(bool(item["ConstraintViolation"]) for item in cal)
        failure_ci = wilson_interval(failures, expected)
        violation_ci = wilson_interval(violations, expected)
        histories = [str(item["ActionHistory"]) for item in acts]
        any_monitoring = sum("IntensifiedMonitoring" in history.split("->") for history in histories)
        any_replacement = sum("PlannedReplacement" in history.split("->") for history in histories)
        direct_replacement = sum(history.split("->")[0] == "PlannedReplacement" for history in histories)
        delayed_replacement = sum(history.split("->")[0] != "PlannedReplacement" and "PlannedReplacement" in history.split("->")[1:] for history in histories)
        targeted_inspection = sum("TargetedInspection" in history.split("->") for history in histories)
        first_continue = sum(history.split("->")[0] == "Continue" for history in histories)
        first_monitoring = sum(history.split("->")[0] == "IntensifiedMonitoring" for history in histories)
        decision_map = first_decisions(selected_decision_path(run_root, row_id))
        if len(decision_map) != expected:
            raise ValueError(f"First-decision count mismatch for {row_id}")
        observed_map = {item["ComponentEpisodeId"]: int(bool(item["ObservedFirstPassage"])) for item in cal}
        probabilities: list[float] = []
        outcomes: list[int] = []
        selected_constraint_breaches = 0
        selected_probability_over_epsilon = 0
        for episode in sorted(decision_map):
            record = decision_map[episode]
            probability = float(record["selected_failure_probability"])
            if not 0.0 <= probability <= 1.0:
                raise ValueError("Selected failure probability outside [0,1]")
            probabilities.append(probability)
            outcomes.append(observed_map[episode])
            selected_constraint_breaches += int(parse_bool(record["constraint_breach"]))
            selected_probability_over_epsilon += int(probability > EPSILON + 1.0e-10)
        brier = mean([(p - y) ** 2 for p, y in zip(probabilities, outcomes)]) if incomplete == 0 else math.nan
        scores = [safe_log_score(p, y) for p, y in zip(probabilities, outcomes)] if incomplete == 0 else [math.nan]
        log_score = mean(scores) if all(math.isfinite(score) for score in scores) else math.inf
        mean_predicted = mean(probabilities)
        observed_rate = failures / expected
        summary = {
            "row_id": row_id,
            "episodes": expected,
            "failure_count": failures,
            "failure_rate": observed_rate,
            "failure_ci_low": failure_ci[0],
            "failure_ci_high": failure_ci[1],
            "constraint_violation_count": violations,
            "constraint_violation_rate": violations / expected,
            "constraint_ci_low": violation_ci[0],
            "constraint_ci_high": violation_ci[1],
            "selected_constraint_breach_count": selected_constraint_breaches,
            "selected_probability_over_epsilon_count": selected_probability_over_epsilon,
            "incomplete_count": incomplete,
            "brier_score": brier,
            "log_score": log_score,
            "mean_predicted_failure": mean_predicted,
            "calibration_gap": mean_predicted - observed_rate,
            "monitoring_count": any_monitoring,
            "monitoring_rate": any_monitoring / expected,
            "replacement_count": any_replacement,
            "replacement_rate": any_replacement / expected,
            "direct_replacement_count": direct_replacement,
            "direct_replacement_rate": direct_replacement / expected,
            "delayed_replacement_count": delayed_replacement,
            "delayed_replacement_rate": delayed_replacement / expected,
            "targeted_inspection_count": targeted_inspection,
            "first_continue_count": first_continue,
            "first_monitoring_count": first_monitoring,
        }
        row_summaries[row_id] = summary
        for metric, count in (
            ("observed_first_passage", failures),
            ("constraint_violation", violations),
            ("any_monitoring", any_monitoring),
            ("any_planned_replacement", any_replacement),
            ("direct_planned_replacement", direct_replacement),
            ("delayed_planned_replacement", delayed_replacement),
            ("targeted_inspection", targeted_inspection),
        ):
            ci = wilson_interval(count, expected)
            register.append({
                "evidence_class": EVIDENCE_CLASS,
                "record_type": "ROW_PROPORTION",
                "row_id": row_id,
                "metric": metric,
                "count": count,
                "denominator": expected,
                "value": format(count / expected, ".17g"),
                "ci_low": format(ci[0], ".17g"),
                "ci_high": format(ci[1], ".17g"),
                "status": "verified" if incomplete == 0 else "requires verification",
            })
        for metric, value in (
            ("brier_score", brier),
            ("log_score", log_score),
            ("mean_predicted_failure", mean_predicted),
            ("calibration_gap_predicted_minus_observed", mean_predicted - observed_rate),
        ):
            register.append({
                "evidence_class": EVIDENCE_CLASS,
                "record_type": "CALIBRATION_SUMMARY",
                "row_id": row_id,
                "metric": metric,
                "count": "",
                "denominator": expected,
                "value": format(value, ".17g") if math.isfinite(value) else "NotEvaluable",
                "ci_low": "",
                "ci_high": "",
                "status": "verified" if incomplete == 0 and math.isfinite(value) else "not claimed",
            })
        bins: dict[str, list[tuple[float, int]]] = defaultdict(list)
        for record, probability, outcome in zip(cal, probabilities, outcomes):
            bins[str(record["CalibrationBin"])].append((probability, outcome))
        for bin_id in sorted(bins, key=lambda item: float(item)):
            pairs = bins[bin_id]
            observed = sum(item[1] for item in pairs)
            ci = wilson_interval(observed, len(pairs))
            register.append({
                "evidence_class": EVIDENCE_CLASS,
                "record_type": "CALIBRATION_BIN",
                "row_id": row_id,
                "metric": f"calibration_bin_{bin_id}",
                "count": observed,
                "denominator": len(pairs),
                "value": format(mean([item[0] for item in pairs]), ".17g"),
                "ci_low": format(ci[0], ".17g"),
                "ci_high": format(ci[1], ".17g"),
                "status": "verified" if incomplete == 0 else "not claimed",
            })
    control = first_decisions(run_root / "raw" / "T6-ABL1--control" / "SCH-DEC.csv")
    variant = first_decisions(run_root / "raw" / "T6-ABL1--ABL-1" / "SCH-DEC.csv")
    if set(control) != set(variant) or len(control) != EXPECTED_EPISODES["T6-ABL1"]:
        raise ValueError("ABL-1 paired decision set mismatch")
    changed = sum(control[key]["selected_first_action"] != variant[key]["selected_first_action"] for key in control)
    paired_decision = {
        "paired_count": len(control),
        "changed_count": changed,
        "changed_rate": changed / len(control),
        "control_information_action_count": sum(value["selected_first_action"] == "IntensifiedMonitoring" for value in control.values()),
        "variant_information_action_count": sum(value["selected_first_action"] == "IntensifiedMonitoring" for value in variant.values()),
        "control_constraint_breach_count": sum(parse_bool(value["constraint_breach"]) for value in control.values()),
        "control_probability_over_epsilon_count": sum(float(value["selected_failure_probability"]) > EPSILON + 1.0e-10 for value in control.values()),
    }
    register.append({
        "evidence_class": EVIDENCE_CLASS,
        "record_type": "PAIRED_DECISION_CHANGE",
        "row_id": "T6-ABL1",
        "metric": "control_vs_no_information_first_action_change",
        "count": changed,
        "denominator": len(control),
        "value": format(changed / len(control), ".17g"),
        "ci_low": format(wilson_interval(changed, len(control))[0], ".17g"),
        "ci_high": format(wilson_interval(changed, len(control))[1], ".17g"),
        "status": "verified",
    })
    return register, paired_decision, row_summaries


def analyze_ablations(run_root: Path) -> tuple[list[dict], dict[str, dict]]:
    records = read_jsonl(run_root / "ablations" / "ablation-inputs.jsonl")
    grouped: dict[str, list[dict]] = defaultdict(list)
    for record in records:
        grouped[record["RowId"]].append(record)
    rows: list[dict] = []
    summaries: dict[str, dict] = {}
    for row_id in ABLATION_VARIANTS:
        group = sorted(grouped[row_id], key=lambda item: item["ComponentEpisodeId"])
        if len(group) != EXPECTED_EPISODES[row_id] or len({item["ComponentEpisodeId"] for item in group}) != len(group):
            raise ValueError(f"Ablation episode count/uniqueness mismatch for {row_id}")
        values = [float(item["Difference"]) for item in group]
        for item, value in zip(group, values):
            if abs((float(item["ControlLoss"]) - float(item["AblatedLoss"])) - value) > 1.0e-12:
                raise ValueError(f"Ablation difference mismatch for {row_id}")
        ci = percentile_interval(values, row_id)
        summary = {
            "evidence_class": EVIDENCE_CLASS,
            "record_type": "ABLATION_PAIRED_EFFECT",
            "row_id": row_id,
            "metric": "control_loss_minus_ablated_loss",
            "count": len(values),
            "denominator": len(values),
            "value": format(mean(values), ".17g"),
            "ci_low": format(ci[0], ".17g"),
            "ci_high": format(ci[1], ".17g"),
            "status": "verified",
            "median": format(statistics.median(values), ".17g"),
            "sd": format(statistics.stdev(values), ".17g"),
            "mad_scale": format(robust_scale_mad(values), ".17g"),
            "semantics": group[0]["Semantics"],
        }
        rows.append(summary)
        summaries[row_id] = summary
    return rows, summaries


def semantic_raw_qa(run_root: Path) -> dict:
    decision_paths = sorted(run_root.glob("raw/*/SCH-DEC.csv"))
    consequence_paths = sorted(run_root.glob("raw/*/SCH-CON.csv"))
    if not decision_paths or not consequence_paths:
        raise ValueError("Mandatory decision/consequence raw records absent")
    oracle_rows = 0
    constraint_breaches = 0
    for path in decision_paths:
        for row in read_csv(path):
            oracle_rows += int(row["oracle_access_count"] != "0")
            constraint_breaches += int(parse_bool(row["constraint_breach"]))
    consequence_rows = 0
    consequence_mismatches = 0
    for path in consequence_paths:
        for row in read_csv(path):
            consequence_rows += 1
            expected = (
                0.47 * int(parse_bool(row["failure_occurred"]))
                + 0.21 * float(row["planned_replacement_burden"])
                + 0.09 * float(row["monitor_burden"])
                + 0.08 * float(row["inspection_burden"])
                + 0.15 * float(row["downtime_burden"])
            )
            consequence_mismatches += int(abs(expected - float(row["realized_disutility"])) > 1.0e-12)
    fallback_summary = json.loads((run_root / "numerical" / "deterministic-fallback-summary.json").read_text(encoding="utf-8"))
    fallback_calls = read_jsonl(run_root / "numerical" / "deterministic-fallback-calls.jsonl")
    protocol_calls = read_jsonl(run_root / "numerical" / "protocol-v2-calls.jsonl")
    false_feasible = sum("false_feasible" in json.dumps(item, sort_keys=True).lower() and bool(item.get("FalseFeasible", False)) for item in protocol_calls)
    return {
        "decision_file_count": len(decision_paths),
        "decision_oracle_access_nonzero_count": oracle_rows,
        "decision_constraint_breach_count_all_policies": constraint_breaches,
        "consequence_file_count": len(consequence_paths),
        "consequence_record_count": consequence_rows,
        "consequence_reproduction_mismatch_count": consequence_mismatches,
        "fallback_summary_call_count": fallback_summary.get("FallbackCallCount"),
        "fallback_record_count": len(fallback_calls),
        "protocol_v2_record_count": len(protocol_calls),
        "false_feasible_promotion_count": false_feasible,
    }


def resolve_claims(primary_rows: list[dict], paired_decision: dict, row_summaries: dict[str, dict], semantic_qa: dict) -> dict:
    eligible = [row["baseline_id"] for row in primary_rows if row["claim_eligible"] == "YES"]
    claim1 = "CLAIM_1_FULLY_SUPPORTED" if len(eligible) == 3 else ("CLAIM_1_PARTIALLY_SUPPORTED" if eligible else "CLAIM_1_NOT_SUPPORTED")
    s0 = row_summaries["T6-S0-PRIMARY"]
    claim2_supported = (
        s0["monitoring_count"] > 0
        and paired_decision["changed_count"] > 0
        and paired_decision["control_information_action_count"] > 0
        and paired_decision["control_constraint_breach_count"] == 0
        and paired_decision["control_probability_over_epsilon_count"] == 0
        and s0["targeted_inspection_count"] == 0
    )
    claim2_descriptive = s0["monitoring_count"] > 0 or paired_decision["changed_count"] > 0
    claim2 = "CLAIM_2_SUPPORTED" if claim2_supported else ("CLAIM_2_DESCRIPTIVE_ONLY" if claim2_descriptive else "CLAIM_2_NOT_SUPPORTED")
    rates = [row_summaries[row]["replacement_rate"] for row in ("T6-S0-PRIMARY", "T6-S1-SEV050", "T6-S2-SEV075")]
    if rates[0] < rates[1] < rates[2]:
        claim3 = "CLAIM_3_SUPPORTED_ROBUSTNESS"
    elif (rates[0] <= rates[1] <= rates[2] and len(set(rates)) > 1) or rates[0] < rates[1] or rates[1] < rates[2]:
        claim3 = "CLAIM_3_PARTIALLY_SUPPORTED"
    else:
        claim3 = "CLAIM_3_NOT_SUPPORTED"
    semantic_pass = (
        semantic_qa["decision_oracle_access_nonzero_count"] == 0
        and semantic_qa["consequence_reproduction_mismatch_count"] == 0
        and semantic_qa["fallback_summary_call_count"] == semantic_qa["fallback_record_count"]
        and semantic_qa["false_feasible_promotion_count"] == 0
    )
    principal_defensible = claim1 != "CLAIM_1_NOT_SUPPORTED" or claim2 == "CLAIM_2_SUPPORTED" or claim3 != "CLAIM_3_NOT_SUPPORTED"
    return {
        "schema_version": SCHEMA_VERSION,
        "evidence_class": EVIDENCE_CLASS,
        "claim_1": claim1,
        "claim_1_supported_baselines": eligible,
        "claim_2": claim2,
        "claim_3": claim3,
        "severity_replacement_rates_s0_s1_s2": rates,
        "semantic_qa_pass": semantic_pass,
        "principal_scientific_claim_defensible": principal_defensible,
        "system_safety_boundary": "one indexed cutter position; no whole-machine, mine-safety, personnel-safety or accident-reduction claim",
    }


def analyze(run_root: Path, output_dir: Path) -> None:
    completion = validate_completion(run_root)
    primary, influence, _ = analyze_primary(run_root)
    raw_p = {item.contrast_id: item.raw_p for item in primary}
    adjusted, rejected = holm_adjust(raw_p)
    primary_rows: list[dict] = []
    for item in primary:
        eligible = item.mean_difference < 0.0 and item.ci_high < 0.0 and adjusted[item.contrast_id] < ALPHA and rejected[item.contrast_id]
        primary_rows.append({
            "evidence_class": EVIDENCE_CLASS,
            "contrast_id": item.contrast_id,
            "baseline_id": item.baseline_id,
            "paired_episode_count": item.n,
            "mean_difference": format(item.mean_difference, ".17g"),
            "median_difference": format(item.median_difference, ".17g"),
            "sd_difference": format(item.sd_difference, ".17g"),
            "robust_scale_mad": format(item.mad_scale, ".17g"),
            "bootstrap_replicates": BOOTSTRAP_REPLICATES,
            "ci_95_low": format(item.ci_low, ".17g"),
            "ci_95_high": format(item.ci_high, ".17g"),
            "inferential_statistic": "paired_two_sided_t_test",
            "t_statistic": format(item.t_statistic, ".17g"),
            "degrees_of_freedom": item.df,
            "unadjusted_p_two_sided": format(item.raw_p, ".17g"),
            "holm_adjusted_p": format(adjusted[item.contrast_id], ".17g"),
            "holm_reject": "YES" if rejected[item.contrast_id] else "NO",
            "relation_to_zero": "CI_STRICTLY_BELOW_ZERO" if item.ci_high < 0.0 else ("CI_STRICTLY_ABOVE_ZERO" if item.ci_low > 0.0 else "CI_INCLUDES_ZERO"),
            "delta_star": DELTA_STAR,
            "delta_star_relation": delta_relation(item.mean_difference),
            "claim_eligible": "YES" if eligible else "NO",
        })
    reliability_rows, paired_decision, row_summaries = reliability_action_rows(run_root)
    ablation_rows, ablation_summaries = analyze_ablations(run_root)
    semantic_qa = semantic_raw_qa(run_root)
    claims = resolve_claims(primary_rows, paired_decision, row_summaries, semantic_qa)
    claims["paired_information_decision_evidence"] = paired_decision
    claims["semantic_raw_qa"] = semantic_qa
    claims["row_summaries"] = row_summaries
    claims["ablation_summaries"] = ablation_summaries
    write_csv_exclusive(output_dir / "ER-07B-R2F_1152_PRIMARY_STATISTICAL_RESULT_REGISTER_2026-08-21.csv", list(primary_rows[0].keys()), primary_rows)
    write_csv_exclusive(output_dir / "ER-07B-R2F_1152_INFLUENTIAL_EPISODE_REGISTER_2026-08-21.csv", list(influence[0].keys()), influence)
    combined = reliability_rows + ablation_rows
    combined_fields = ["evidence_class", "record_type", "row_id", "metric", "count", "denominator", "value", "ci_low", "ci_high", "status", "median", "sd", "mad_scale", "semantics"]
    write_csv_exclusive(output_dir / "ER-07B-R2F_1152_RELIABILITY_ACTION_ABLATION_REGISTER_2026-08-21.csv", combined_fields, combined)
    write_json_exclusive(output_dir / "ER-07B-R2F_1152_CLAIM_RESOLUTION_MACHINE_2026-08-21.json", claims)
    input_paths = [
        run_root / "completion-manifest.json",
        run_root / "statistics" / "paired-contrast-inputs.jsonl",
        run_root / "statistics" / "calibration-reliability-inputs.jsonl",
        run_root / "actions" / "action-records.jsonl",
        run_root / "ablations" / "ablation-inputs.jsonl",
    ]
    manifest = {
        "schema_version": SCHEMA_VERSION,
        "analysis_script_sha256": sha256_file(Path(__file__)),
        "evidence_class": EVIDENCE_CLASS,
        "run_root": str(run_root.resolve()),
        "run_id": completion.get("RunId", run_root.name),
        "bootstrap_replicates": BOOTSTRAP_REPLICATES,
        "bootstrap_rng": "sha256-xoshiro256starstar-v1; purpose=BootstrapResample(10); per-replicate stream",
        "bootstrap_percentile_method": "linear interpolation at p*(B-1)",
        "primary_family": list(PRIMARY_CONTRASTS),
        "alpha_family": ALPHA,
        "delta_star": DELTA_STAR,
        "input_hashes": {str(path.relative_to(run_root)).replace(os.sep, "/"): sha256_file(path) for path in input_paths},
        "source_sha256": T6_SOURCE_SHA256,
        "executable_sha256": T6_EXECUTABLE_SHA256,
        "config_sha256": T6_CONFIG_SHA256,
        "authorization_sha256": T6_AUTHORIZATION_SHA256,
        "authorization_id": T6_AUTHORIZATION_ID,
        "claims": {key: claims[key] for key in ("claim_1", "claim_2", "claim_3", "semantic_qa_pass", "principal_scientific_claim_defensible")},
    }
    write_json_exclusive(output_dir / "ER-07B-R2F_1152_FROZEN_ANALYSIS_MANIFEST_2026-08-21.json", manifest)


def self_test() -> None:
    assert abs(paired_t_two_sided([-2.0, -1.0, 0.0, 1.0, 2.0])[2] - 1.0) < 1.0e-14
    p_at_critical = regularized_incomplete_beta(4.0 / (4.0 + 2.7764451051977987**2), 2.0, 0.5)
    assert abs(p_at_critical - 0.05) < 2.0e-8
    adjusted, rejected = holm_adjust({"A": 0.01, "B": 0.03, "C": 0.04})
    assert abs(adjusted["A"] - 0.03) < 1.0e-15
    assert abs(adjusted["B"] - 0.06) < 1.0e-15
    assert abs(adjusted["C"] - 0.06) < 1.0e-15
    assert rejected == {"A": True, "B": False, "C": False}
    fixture = [-0.4, -0.3, -0.2, -0.1]
    first = bootstrap_means(fixture, "SELF-TEST", 50)
    second = bootstrap_means(fixture, "SELF-TEST", 50)
    assert first == second and all(-0.4 <= value <= -0.1 for value in first)
    assert abs(mean(fixture) + 0.25) < 1.0e-15
    assert delta_relation(-0.03) == "FAVOURS_P-R4_AT_LEAST_DELTA_STAR"
    low, high = wilson_interval(0, 256)
    assert low == 0.0 and 0.0 < high < 0.02
    print(json.dumps({"schema_version": SCHEMA_VERSION, "self_test": "PASS", "tests": 12}, sort_keys=True))


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--run-root", type=Path)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args(argv)
    if args.self_test:
        self_test()
        return 0
    if args.run_root is None or args.output_dir is None:
        parser.error("--run-root and --output-dir are required unless --self-test is used")
    analyze(args.run_root, args.output_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
