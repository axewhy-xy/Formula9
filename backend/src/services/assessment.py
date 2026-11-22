"""Versioned rules for the scored Toa Payoh exercise.

Author: Nur Syazliyarni Binte Zazali
Date Created: 15/11/2026
Last Modified: 23/11/2026

Evaluators read the bounded, committed event journal at finalization. They
return points (or unavailable), a plain-language reason and evidence IDs.
The scoring service applies the pass mark and safety overrides once.
"""

from collections import defaultdict
from collections.abc import Callable, Iterable
from dataclasses import InitVar, dataclass, field
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, FiniteFloat, NonNegativeInt, PositiveInt, model_validator

from contracts.v2.assessment import AssessmentCriterion, AssessmentFailure, SessionAssessment
from contracts.v2.models import Event, FullSnapshot, Id

SCORED_SCENARIO = "scenario-toa-payoh-scored-r1"
SCORED_INCIDENT = "assessment-road-block"
RUBRIC_PATH = Path(__file__).resolve().parents[2] / "fixtures" / "toa_payoh_assessment.json"
CHARGING_PLAN_EVENTS = frozenset(("CHARGER_RESERVED", "SERVICE_CONTINUED", "CHARGE_SCHEDULED",
                                 "CHARGE_BOOKING_STARTED", "CHARGE_BOOKING_CANCELLED"))


class RuleSettings(BaseModel):
    """Common point settings plus the selected evaluator's own parameters."""

    model_config = ConfigDict(extra="allow", strict=True)
    maximum: PositiveInt
    partial: NonNegativeInt = 0


class ShortExercise(BaseModel):
    """Observed milestones that fit inside a short exercise on the full road network."""

    model_config = ConfigDict(extra="forbid", strict=True)
    stop_id: Id
    approach_m: PositiveInt
    incident_service_m: PositiveInt
    charging_trip_m: PositiveInt


class Rubric(BaseModel):
    """Authored weights, safety checks and fixed exercise boundaries."""

    model_config = ConfigDict(extra="forbid", strict=True)
    metadata: dict[str, str]
    revision: Id
    evaluator_version: Id
    identity_version: Literal["semantic-v1"] | None = None
    pass_mark: PositiveInt
    rules: dict[str, RuleSettings]
    automatic_fails: list[str]
    seed: NonNegativeInt
    route_id: Id
    route_vehicle_id: Id
    charging_vehicle_id: Id
    charging_initial_kwh: FiniteFloat
    charging_offer_loops: PositiveInt
    incident_start_delay_s: PositiveInt
    incident_duration_s: PositiveInt
    charging_decision_s: PositiveInt
    charging_observation_s: PositiveInt
    charging_offer_deadline_s: PositiveInt | None = None
    safety_opportunity_s: PositiveInt
    exercise_limit_s: PositiveInt
    short_exercise: ShortExercise

    @model_validator(mode="after")
    def validate_rules(self):
        """Reject inconsistent rules before an attempt is admitted."""
        version = evaluators(self.evaluator_version)
        if self.rules.keys() - version.criteria.keys() or set(self.automatic_fails) - version.safety.keys():
            raise ValueError("Rubric names an unknown assessment rule")
        if len(set(self.automatic_fails)) != len(self.automatic_fails):
            raise ValueError("Duplicate automatic failure rule")
        if sum(rule.maximum for rule in self.rules.values()) != 100 or self.pass_mark > 100:
            raise ValueError("Rubric must total 100 points with a valid pass mark")
        for name, settings in self.rules.items():
            definition = version.criteria[name]
            parameters = definition.parameters
            if settings.partial > settings.maximum or settings.model_extra.keys() != parameters.keys():
                raise ValueError(f"Invalid parameters or partial credit for {name}")
            for key, check in parameters.items():
                if not check(settings.model_extra[key]):
                    raise ValueError(f"Invalid {key} for {name}")
            if definition.validate is not None:
                definition.validate(self, settings)
        if not (0 < self.charging_initial_kwh <= 40 and self.safety_opportunity_s <= self.charging_decision_s
                < self.charging_observation_s <= self.exercise_limit_s):
            raise ValueError("Invalid charging or exercise window")
        if (self.charging_offer_deadline_s is not None and
                self.charging_offer_deadline_s + self.charging_observation_s > self.exercise_limit_s):
            raise ValueError("The latest charging offer and observation window must fit inside the exercise")
        return self


class AssessmentPlan(BaseModel):
    """Copy the rubric and validated route reference into the attempt at creation."""

    rubric: Rubric
    required_stops: list[Id] = Field(min_length=1)
    reference_duration_s: FiniteFloat = Field(gt=0)
    reference_energy_kwh: FiniteFloat = Field(gt=0)
    step_s: FiniteFloat = Field(gt=0)

    def ticks(self, seconds: float) -> int:
        """Convert an authored time window to the simulator's fixed clock."""
        return round(seconds / self.step_s)


def load_rubric() -> Rubric:
    """Load the rubric offered to new attempts."""
    return Rubric.model_validate_json(RUBRIC_PATH.read_bytes())


@dataclass
class Evidence:
    """Index committed events for the assessed route, incident and charging tasks."""

    plan: AssessmentPlan
    snapshot: FullSnapshot
    events: InitVar[Iterable[Event]]
    approval: Event | None = field(init=False)
    incident_detected: Event | None = field(init=False)
    incident_offer: Event | None = field(init=False)
    incident_decision: Event | None = field(init=False)
    charge_offer: Event | None = field(init=False)
    charge_plan: Event | None = field(init=False)
    charge_observed: Event | None = field(init=False)
    by_type: dict[str, list[Event]] = field(init=False, repr=False)
    charging_events: list[Event] = field(init=False, repr=False)

    def __post_init__(self, events):
        rubric = self.plan.rubric
        self.by_type = defaultdict(list)
        # The journal arrives in sequence order. Index charging decisions for both cutoffs in this pass.
        self.charging_events = []
        for event in events:
            kind = event.event_type
            self.by_type[kind].append(event)
            if kind in CHARGING_PLAN_EVENTS and event.data.get("vehicle_id", event.entity_id) == rubric.charging_vehicle_id:
                self.charging_events.append(event)
        self.approval = self.first("ROUTE_APPROVED", rubric.route_id, latest=True)
        self.incident_detected = self.first("INCIDENT_DETECTED", SCORED_INCIDENT)
        self.incident_offer = next((e for e in self.find("OPTIONS_READY", entity=SCORED_INCIDENT)
                                    if any(option.get("vehicle_id") == rubric.route_vehicle_id
                                           and option.get("validation") in {"PASS", "WARN"}
                                           for option in e.data.get("options", {}).values())), None)
        self.incident_decision = next((e for e in self.find("INCIDENT_DECISION_RECORDED", entity=SCORED_INCIDENT)
                                       if e.data.get("vehicle_id") == rubric.route_vehicle_id), None)
        self.charge_offer = self.first("ASSESSMENT_CHARGING_OFFERED", rubric.charging_vehicle_id)
        self.charge_observed = self.first("ASSESSMENT_CHARGING_OBSERVED", rubric.charging_vehicle_id)
        self.charge_plan = self.charging_plan_at(
            self.charge_offer.tick + self.plan.ticks(rubric.charging_decision_s)
        ) if self.charge_offer else None

    def first(self, kind: str, entity: str, *, latest: bool = False) -> Event | None:
        """Read a single matching event without building a list of its peers."""
        matches = self.by_type.get(kind, ())
        for event in reversed(matches) if latest else matches:
            if event.entity_id == entity:
                return event
        return None

    def find(self, *kinds: str, entity: str | None = None) -> list[Event]:
        """Find relevant events without rescanning unrelated journal entries for every rule."""
        matches = [event for kind in kinds for event in self.by_type.get(kind, ())
                   if entity is None or event.entity_id == entity]
        if len(kinds) > 1:
            matches.sort(key=lambda event: event.event_sequence)
        return matches

    def charging_plan_at(self, tick: int, *, before_sequence: int | None = None) -> Event | None:
        """Take the latest plan for the assigned shuttle, accounting for reschedules and cancellations."""
        booked = continued = None
        for event in self.charging_events:
            if event.tick > tick or (before_sequence is not None and event.event_sequence >= before_sequence):
                break
            if event.event_type == "SERVICE_CONTINUED":
                continued = event
            elif event.event_type == "CHARGE_SCHEDULED":
                booked = event
            elif event.event_type == "CHARGER_RESERVED":
                booked, continued = event, None
            elif booked is not None and event.data.get("booking_id") == booked.data.get("booking_id"):
                if event.event_type == "CHARGE_BOOKING_STARTED":
                    continued = None
                else:
                    # Cancelling an undispatched slot leaves the last continue decision in effect.
                    booked = None
        return booked or continued


Result = tuple[float | None, str, list[Event]]


def route_approval(e: Evidence, rule: RuleSettings) -> Result:
    if e.approval:
        return rule.maximum, "A feasible detour was approved before departure.", [e.approval]
    if e.snapshot.session.status == "COMPLETED":
        return 0, "The required route approval was missed.", []
    return None, "The required route approval was not recorded before this attempt ended.", []


def designated_stop(e: Evidence, rule: RuleSettings) -> Result:
    """Score the stop on the assessed section of the approved route."""
    stop_id = e.plan.rubric.short_exercise.stop_id
    served = next((event for event in e.find("STOP_SERVED", entity=e.plan.rubric.route_vehicle_id)
                   if e.approval and event.event_sequence > e.approval.event_sequence
                   and event.data.get("stop_id") == stop_id and event.data.get("route_id") == e.plan.rubric.route_id), None)
    if served:
        return rule.maximum, f"Served the designated stop ({stop_id}).", [e.approval, served]
    if e.snapshot.session.status == "COMPLETED":
        return 0, f"The designated stop ({stop_id}) was not served by the end of the exercise.", []
    return None, "The designated stop had not been served when the attempt ended.", []


def efficiency(metric: str, reference: str, units: str) -> Callable[[Evidence, RuleSettings], Result]:
    """Both efficiency criteria compare the committed route estimate to a feasible reference."""
    def evaluate(e: Evidence, rule: RuleSettings) -> Result:
        value = ((e.approval.data.get("edited") or {}).get(metric) if e.approval else None)
        if value is None:
            return None, "A committed detour estimate is unavailable.", []
        expected = getattr(e.plan, reference)
        ratio = value / expected
        points = rule.maximum if ratio <= rule.full_ratio else rule.partial if ratio <= rule.partial_ratio else 0
        return points, f"Estimated {value:.2f} {units} against {expected:.2f} {units} ({ratio:.1%} of reference).", [e.approval]
    return evaluate


def missed_incident(e: Evidence) -> Result:
    """A missed response is zero only once the offered incident decision has closed."""
    outcome = e.find("INCIDENT_RESOLVED", "INCIDENT_UNRESOLVED", entity=SCORED_INCIDENT)
    if e.incident_offer and outcome:
        return 0, "A response was offered and the incident ended without a decision.", [e.incident_offer, outcome[-1]]
    return None, "The incident decision window did not have a complete record.", []


def incident_choice(e: Evidence, rule: RuleSettings) -> Result:
    if not e.incident_decision:
        return missed_incident(e)
    action = e.incident_decision.data["action"]
    return _incident_choice_points(action, rule), f"Applied response {action.replace('_', ' ').lower()}.", [e.incident_decision]


def _incident_choice_points(action: str, rule: RuleSettings) -> int:
    """Apply the same credit tiers when deciding eligibility for timing points."""
    return rule.maximum if action in rule.full_actions else rule.partial if action in rule.partial_actions else 0


def incident_time(e: Evidence, rule: RuleSettings) -> Result:
    if not e.incident_decision:
        return missed_incident(e)
    if not e.incident_offer or not e.incident_detected:
        return None, "No timestamp for the first actionable incident offer.", [e.incident_decision]
    response_rule = e.plan.rubric.rules.get("incident_choice")
    if response_rule and _incident_choice_points(e.incident_decision.data["action"], response_rule) == 0:
        return 0, "Timing credit requires a creditworthy response.", [e.incident_decision]
    step = e.plan.step_s
    deadline = (e.incident_detected.data["active_until_tick"] - e.incident_offer.tick) * step
    zero_s = min(deadline, rule.zero_s)
    full_s = rule.full_s if deadline >= rule.zero_s else min(rule.full_s, zero_s / 2)
    elapsed = max(0, e.incident_decision.tick - e.incident_offer.tick) * step
    share = max(0, min(1, (zero_s - elapsed) / (zero_s - full_s))) if zero_s > 0 else 0
    return rule.maximum * share, f"Applied {elapsed:.1f} simulation seconds after the offer.", [e.incident_offer, e.incident_decision]


def incident_service_resumed(e: Evidence, rule: RuleSettings) -> Result:
    """Grade observed service after a detour or once the road reopens."""
    if not e.incident_decision:
        return missed_incident(e)
    if not e.incident_offer:
        return None, "No timestamp for the first actionable incident offer.", [e.incident_decision]
    resumed = e.first("ASSESSMENT_SERVICE_RESUMED", e.plan.rubric.route_vehicle_id)
    if resumed is None:
        if e.snapshot.session.status == "COMPLETED":
            return 0, "The shuttle did not cover the required distance after the response.", [e.incident_decision]
        return None, "The attempt ended before service resumption was observed.", [e.incident_decision]
    elapsed = (resumed.data.get("observed_tick", resumed.tick) - e.incident_offer.tick) * e.plan.step_s
    points = rule.maximum if elapsed <= rule.full_s else rule.partial if elapsed <= rule.partial_s else 0
    return points, f"Covered {resumed.data['distance_m']} m in service {elapsed:.1f} s after the offer.", [
        e.incident_offer, e.incident_decision, resumed]


def charge_evidence(e: Evidence) -> Result | None:
    if not e.charge_offer:
        return None, "The simulator never recorded an actionable charging offer for this shuttle.", []
    if not e.charge_plan:
        if e.snapshot.tick >= e.charge_offer.tick + e.plan.ticks(e.plan.rubric.charging_decision_s):
            return 0, "No charging or continued-service plan was in effect by the decision deadline.", [e.charge_offer]
        return None, "The attempt ended before the charging decision deadline.", [e.charge_offer]
    if not e.charge_observed:
        return None, "The charging observation window did not finish.", [e.charge_plan]
    return None


def battery_reserve(e: Evidence, rule: RuleSettings) -> Result:
    if (missing := charge_evidence(e)) is not None:
        return missing
    data = e.charge_observed.data
    low, reserve, capacity = data["minimum_kwh"], data["reserve_kwh"], data["capacity_kwh"]
    points = rule.maximum if low >= reserve + rule.buffer_fraction * capacity else rule.partial if low >= reserve else 0
    return points, f"Lowest charge {low:.2f} kWh against a {reserve:.2f} kWh reserve.", [e.charge_plan, e.charge_observed]


def fleet_coverage(e: Evidence, rule: RuleSettings) -> Result:
    if (missing := charge_evidence(e)) is not None:
        return missing
    low = e.charge_observed.data["minimum_in_service"]
    points = rule.maximum if low >= rule.full_count else rule.partial if low >= rule.partial_count else 0
    return points, f"At least {low} shuttles remained assigned to active service during the charging window.", [e.charge_plan, e.charge_observed]


def charging_follow_through(e: Evidence, rule: RuleSettings) -> Result:
    """Verify the planned charging trip or continued-service outcome."""
    if (missing := charge_evidence(e)) is not None:
        return missing
    plan = e.charge_plan
    vehicle = e.plan.rubric.charging_vehicle_id
    if plan.event_type == "SERVICE_CONTINUED":
        deadline = plan.tick + e.plan.ticks(rule.continue_s)
        if plan.data.get("battery_after_kwh", float("inf")) < e.charge_observed.data["reserve_kwh"]:
            return 0, "Continued service began below the minimum reserve.", [plan, e.charge_observed]
        interruption = next((event for event in e.find("SAFETY_RETURN", "BATTERY_EXHAUSTED", "CHARGE_BOOKING_STARTED",
                                                      "CHARGER_RESERVED", "ASSESSMENT_BELOW_RESERVE")
                             if event.data.get("vehicle_id", event.entity_id) == vehicle
                             and event.event_sequence > plan.event_sequence and event.tick <= deadline), None)
        if interruption:
            return 0, "The continued-service window was interrupted by a reserve breach or a charging trip.", [plan, interruption]
        return rule.maximum, f"Continued safely for the required {rule.continue_s} s.", [plan, e.charge_observed]
    booking_id = plan.data.get("booking_id")
    short = e.plan.rubric.short_exercise
    moved = next((event for event in e.find("ASSESSMENT_CHARGING_TRIP_MOVED", entity=vehicle)
                  if booking_id and event.data.get("booking_id") == booking_id
                  and plan.event_sequence < event.event_sequence <= e.charge_observed.event_sequence), None)
    if moved:
        return rule.maximum, f"The selected booking drove {short.charging_trip_m} m toward its charger.", [plan, moved]
    return 0, f"The selected booking did not drive {short.charging_trip_m} m toward its charger.", [
        plan, e.charge_observed]


def closed_road_entry(e: Evidence) -> list[AssessmentFailure]:
    """Require both a recorded crossing and an applied route issued with the closure already active."""
    failures = []
    approvals = e.find("ROUTE_APPROVED", "INCIDENT_DECISION_RECORDED")
    for entry in e.find("ASSESSMENT_CLOSED_ROAD_ENTRY"):
        approval = next((event for event in reversed(approvals)
                         if event.event_sequence < entry.event_sequence
                         and event.data.get("applied_route_id", event.entity_id) == entry.data["route_id"]
                         and entry.entity_id in event.data.get("target_vehicle_ids", [event.data.get("vehicle_id")])), None)
        if (approval and entry.data["edge_id"] in approval.data.get("directed_edge_ids", [])
                and approval.tick >= entry.data["closed_from_tick"]):
            failures.append(AssessmentFailure(rule_id="closed_road_entry",
                explanation=f"{entry.entity_id} entered closed road {entry.data['edge_id']} on an applied route.",
                event_ids=[approval.event_id, entry.event_id]))
    return failures


def uncleared_incident_entry(e: Evidence) -> list[AssessmentFailure]:
    """Require a prior opportunity to act and an actual entry before the closure lifted."""
    if not e.incident_offer:
        return []
    after = e.incident_offer.tick + e.plan.ticks(e.plan.rubric.safety_opportunity_s)
    entry = next((event for event in e.find("ASSESSMENT_CLOSED_ROAD_ENTRY", entity=e.plan.rubric.route_vehicle_id)
                  if event.data["incident_id"] == SCORED_INCIDENT and event.data.get("crossing_tick", event.tick) >= after
                  and (not e.incident_decision or e.incident_decision.event_sequence > event.event_sequence
                       or e.incident_decision.data["action"] == "KEEP_ROUTE")), None)
    if not entry:
        return []
    return [AssessmentFailure(rule_id="incident_entry_without_clearance",
        explanation=f"{entry.entity_id} entered the incident area before clearance after an opportunity to act.",
        event_ids=[e.incident_offer.event_id, entry.event_id])]


def battery_exhaustion(e: Evidence) -> list[AssessmentFailure]:
    """Attribute exhaustion only if a usable charge offer persisted long enough to act."""
    vehicle = e.plan.rubric.charging_vehicle_id
    opportunity = e.first("ASSESSMENT_CHARGING_ACTIONABLE", vehicle)
    if not opportunity:
        return []
    exhausted = next((event for event in e.find("BATTERY_EXHAUSTED", entity=vehicle)
                      if event.event_sequence > opportunity.event_sequence), None)
    if not exhausted:
        return []
    plan = e.charging_plan_at(exhausted.tick, before_sequence=exhausted.event_sequence)
    if plan and plan.event_type != "SERVICE_CONTINUED":
        return []
    return [AssessmentFailure(rule_id="preventable_battery_exhaustion",
        explanation=f"{vehicle} ran out of battery after an actionable opportunity to charge.",
        event_ids=[opportunity.event_id, *([plan.event_id] if plan else []), exhausted.event_id])]


def _number(value):
    return type(value) in {int, float} and 0 < value <= 10_000


def _fraction(value):
    return _number(value) and value <= 1


def _count(value):
    return type(value) is int and 0 < value <= 5


def _actions(value):
    return isinstance(value, list) and all(action in ("ROAD_DETOUR", "ESCALATE", "HOLD_AT_STOP", "KEEP_ROUTE") for action in value)


def _ascending(lower: str, upper: str):
    def validate(_: Rubric, rule: RuleSettings):
        if getattr(rule, lower) >= getattr(rule, upper):
            raise ValueError(f"{lower} must be less than {upper}")
    return validate


def _exclusive_incident_actions(_: Rubric, rule: RuleSettings):
    if set(rule.full_actions) & set(rule.partial_actions):
        raise ValueError("An incident action can have only one credit tier")


def _continued_service_window(rubric: Rubric, rule: RuleSettings):
    if rubric.charging_decision_s + rule.continue_s > rubric.charging_observation_s:
        raise ValueError("Continued service must fit inside the charging observation window")


@dataclass(frozen=True)
class Criterion:
    """Bundle a criterion's evaluator, allowed parameters and optional cross-field validation."""

    label: str
    evaluate: Callable[[Evidence, RuleSettings], Result]
    parameters: dict[str, Callable[[object], bool]] = field(default_factory=dict)
    validate: Callable[[Rubric, RuleSettings], None] | None = None


@dataclass(frozen=True)
class EvaluatorVersion:
    """Keep the scoring rules and observation format addressable by a pinned rubric."""

    criteria: dict[str, Criterion]
    safety: dict[str, Callable[[Evidence], list[AssessmentFailure]]]


_EFFICIENCY_PARAMS = {"full_ratio": _number, "partial_ratio": _number}
_EFFICIENCY_THRESHOLDS = _ascending("full_ratio", "partial_ratio")
_SERVICE_PARAMS = {"full_s": _number, "partial_s": _number}
_SERVICE_THRESHOLDS = _ascending("full_s", "partial_s")

RULES_V2 = {
    "route_approval": Criterion("Route approval", route_approval),
    "route_stops": Criterion("Designated stop coverage", designated_stop),
    "route_time": Criterion("Detour time", efficiency("duration_s", "reference_duration_s", "s"),
                            _EFFICIENCY_PARAMS, _EFFICIENCY_THRESHOLDS),
    "route_energy": Criterion("Detour energy", efficiency("energy_kwh", "reference_energy_kwh", "kWh"),
                              _EFFICIENCY_PARAMS, _EFFICIENCY_THRESHOLDS),
    "incident_choice": Criterion("Incident response", incident_choice,
                                 {"full_actions": _actions, "partial_actions": _actions}, _exclusive_incident_actions),
    "incident_time": Criterion("Incident response time", incident_time,
                               {"full_s": _number, "zero_s": _number}, _ascending("full_s", "zero_s")),
    "incident_service": Criterion("Incident service resumption", incident_service_resumed, _SERVICE_PARAMS, _SERVICE_THRESHOLDS),
    "battery_reserve": Criterion("Battery reserve", battery_reserve, {"buffer_fraction": _fraction}),
    "fleet_coverage": Criterion("Fleet coverage during charging", fleet_coverage,
                                {"full_count": _count, "partial_count": _count}, _ascending("partial_count", "full_count")),
    "charging_follow_through": Criterion("Charging follow-through", charging_follow_through,
                                        {"continue_s": _number}, _continued_service_window),
}
SAFETY_RULES = {
    "closed_road_entry": closed_road_entry,
    "incident_entry_without_clearance": uncleared_incident_entry,
    "preventable_battery_exhaustion": battery_exhaustion,
}
EVALUATOR_VERSIONS = {
    "assessment-v2": EvaluatorVersion(RULES_V2, SAFETY_RULES),
}

def evaluators(version: str) -> EvaluatorVersion:
    """Keep evaluator versions addressable by attempts created before a formula changes."""
    selected = EVALUATOR_VERSIONS.get(version)
    if selected is None:
        raise ValueError(f"Unknown assessment evaluator version {version}")
    return selected


def finalize(plan: AssessmentPlan, snapshot: FullSnapshot, events: Iterable[Event]) -> SessionAssessment:
    """Evaluate one committed boundary. Safety failure takes precedence over missing evidence."""
    rubric = plan.rubric
    evidence = Evidence(plan, snapshot, events)
    version = evaluators(rubric.evaluator_version)
    criteria = []
    for name, rule in rubric.rules.items():
        definition = version.criteria[name]
        points, explanation, supporting_events = definition.evaluate(evidence, rule)
        criteria.append(AssessmentCriterion(rule_id=name, label=definition.label, maximum=rule.maximum,
            points=round(points, 2) if points is not None else None, explanation=explanation,
            event_ids=[event.event_id for event in supporting_events]))
    failures = [failure for name in rubric.automatic_fails for failure in version.safety[name](evidence)]
    score = round(sum(criterion.points or 0 for criterion in criteria), 2)
    if failures:
        status, explanation = "FAIL", "A confirmed safety breach overrides the points total."
    elif snapshot.session.status != "COMPLETED" or any(c.points is None for c in criteria):
        status, explanation = "INCOMPLETE", "This attempt ended with incomplete assessment evidence."
    elif score >= rubric.pass_mark:
        status, explanation = "PASS", f"The score meets the {rubric.pass_mark}-point pass mark."
    else:
        status, explanation = "FAIL", f"The score is below the {rubric.pass_mark}-point pass mark."
    return SessionAssessment(session_id=snapshot.session_id, scenario_revision_id=snapshot.session.scenario_revision_id,
        rubric_revision=rubric.revision, duration_s=rubric.exercise_limit_s, status=status, points=score, maximum=100, pass_mark=rubric.pass_mark,
        through_tick=snapshot.tick, through_event_sequence=snapshot.event_sequence, explanation=explanation,
        criteria=criteria, automatic_failures=failures)
