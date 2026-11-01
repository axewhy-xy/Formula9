"""
Battery forecasts and charge options built from a training snapshot.

Author: Choo Jian Wei
Date Created: 24/10/2026
Last Modified: 28/10/2026

Description:
    Part of BE-M2-05 (battery decision). Turns a FullSnapshot into the
    energy_forecasts and charge_options lists of the v2 contract, using the
    energy model (model.py, forecast.py). The native battery screen
    (NAT-M2-06) only displays these values; it never recomputes them.

    These are pure functions with no session state, so the session actor
    (BE-M2-02) can call them for each snapshot it builds, and tests can
    call them on the shared fixtures.

    Legacy fixtures estimate depot travel from position.x_m. The integrated
    actor supplies admitted remaining-path distances and a declared cruise
    speed, including depot access. Its charge estimates account for return
    travel and auxiliary use while charging. All results remain estimates.
"""
from contracts.v2.models import (
    ChargeOption, Depot, EnergyForecast, FleetVehicle, FullSnapshot, RouteRevision, transition_allowed,
)
from .forecast import loop_energy_kwh, loops_remaining, reserve_kwh, reserve_margin_kwh
from .model import EnergyModel, auxiliary_energy_kwh, charge_battery_kwh, drive_energy_kwh, require_non_negative, use_energy

LOW_LOOPS = 3                # at or below this many loops, a shuttle needs a battery decision
IN_SERVICE_STATES = {"IN_SERVICE", "DWELLING", "DETOURING"}  # shuttles actively serving the route
OPTION_LIFETIME_TICKS = 100  # 5 s at 50 ms per tick

"""
# -----------------------------------------------------------------------------------
# Function: forecast_for_vehicle
# -----------------------------------------------------------------------------------
Description:
    This function builds one shuttle's energy forecast: reserve, margin
    above reserve, loops remaining, and the next depot opportunity. The
    depot fields are None when there is no depot or the shuttle is stopped
    (no ETA can be given, and the contract needs an ETA with a depot).

Args:
    model (EnergyModel): The energy parameters.
    vehicle (FleetVehicle): The shuttle, with its battery, position and speed.
    route (RouteRevision): The route the shuttle loops.
    depot (Depot | None): The depot to return to, if any.
    depot_distance_m (float | None): Actor-owned remaining return-path length.
    depot_speed_mps (float | None): Declared return speed for ETA estimation.

Returns:
    EnergyForecast: The forecast, marked as an estimate.
"""
def forecast_for_vehicle(model: EnergyModel, vehicle: FleetVehicle, route: RouteRevision,
                         depot: Depot | None, *, depot_distance_m: float | None = None,
                         depot_speed_mps: float | None = None) -> EnergyForecast:
    if depot_distance_m is not None:
        require_non_negative("depot_distance_m", depot_distance_m)
    if depot_speed_mps is not None:
        require_non_negative("depot_speed_mps", depot_speed_mps)
    capacity = vehicle.battery_capacity_kwh
    margin = reserve_margin_kwh(model, vehicle.battery_kwh, capacity)
    loop = loop_energy_kwh(model, route.distance_m, route.estimated_duration_s)
    depot_id, eta, reaches = None, None, None
    speed = vehicle.speed_mps if depot_speed_mps is None else depot_speed_mps
    if depot is not None and speed > 0:
        depot_id = depot.depot_id
        remaining = (max(0.0, route.distance_m - vehicle.position.x_m)
                     if depot_distance_m is None else max(0.0, depot_distance_m))
        eta = remaining / speed
        reaches = drive_energy_kwh(model, remaining) + auxiliary_energy_kwh(model, eta) <= margin
    return EnergyForecast(
        vehicle_id=vehicle.vehicle_id,
        energy_model_revision=model.revision,
        reserve_kwh=reserve_kwh(model, capacity),
        reserve_margin_kwh=margin,
        loops_remaining=loops_remaining(model, vehicle.battery_kwh, capacity, loop),
        next_depot_id=depot_id,
        next_depot_eta_s=eta,
        reaches_depot_above_reserve=reaches,
    )

"""
# -----------------------------------------------------------------------------------
# Function: energy_forecasts
# -----------------------------------------------------------------------------------
Description:
    This function builds a forecast for every shuttle on a route, in the
    snapshot's vehicle order. Shuttles without a route are skipped, since
    loops cannot be forecast without one.

Args:
    model (EnergyModel): The energy parameters.
    snapshot (FullSnapshot): The training snapshot.
    depot_choices: Optional depot ID by vehicle: the depot each shuttle would
        head for, whose distance depot_distances_m gives. Without it, or for a
        shuttle it does not name, the snapshot's first depot is used.

Returns:
    list[EnergyForecast]: One forecast per routed shuttle.
"""
def energy_forecasts(model: EnergyModel, snapshot: FullSnapshot, *, depot_distances_m: dict[str, float] | None = None,
                      depot_speed_mps: float | None = None,
                      depot_choices: dict[str, str] | None = None) -> list[EnergyForecast]:
    routes = {r.route_id: r for r in snapshot.routes}
    depots = {d.depot_id: d for d in snapshot.depots}
    first = snapshot.depots[0] if snapshot.depots else None
    return [forecast_for_vehicle(model, vehicle, routes[vehicle.route_id],
                                 depots.get((depot_choices or {}).get(vehicle.vehicle_id), first),
                                 depot_distance_m=(depot_distances_m or {}).get(vehicle.vehicle_id),
                                 depot_speed_mps=depot_speed_mps)
            for vehicle in snapshot.vehicles
            if vehicle.route_id is not None]

"""
# -----------------------------------------------------------------------------------
# Function: charge_options
# -----------------------------------------------------------------------------------
Description:
    This function builds the battery decision for every shuttle at or
    below LOW_LOOPS loops that can still head to the depot and is not
    already on a charger (so a shuttle that has just reserved a port gets no
    new choices). A shuttle whose operator chose CONTINUE is not asked again
    until its loops remaining fall below the number recorded in
    snapshot.charge_decision_deferrals. A serving shuttle (IN_SERVICE,
    DWELLING or DETOURING) gets a CONTINUE option (stay in service, battery unchanged)
    and, when a charger has a free port, a CHARGE_NOW option at the first
    such charger by ID. Option IDs include the shuttle, so several low
    shuttles never collide.

    An offer already in the snapshot is kept unchanged while it is
    unexpired and its dependency revisions still match, so the numbers a
    trainee chose are the numbers recorded. Otherwise the option is offered
    with the revision after the last one ever issued for its ID
    (snapshot.charge_option_revisions), even if the offer was withdrawn in
    between; a RESERVE_CHARGER command names the revision it chose, so an
    older choice is rejected as stale.

    offered_at_tick is when the shuttle's current decision was first
    offered. Reissued offers keep it, so the response time runs from the
    first offer rather than the latest reissue. It resets only once the
    shuttle has no offers (for example after a reservation).

Args:
    model (EnergyModel): The energy parameters.
    snapshot (FullSnapshot): The training snapshot.
    charge_s (float): How long CHARGE_NOW keeps the shuttle on the charger,
        in seconds.
    parking_distances_m: Optional bay-to-standby path lengths by vehicle and
        charger. Requires depot distances and a positive declared depot speed.
    depot_choices: Optional depot ID by vehicle. CHARGE_NOW is then offered at
        the first free charger of that depot, the one depot_distances_m
        measures; without it, at the first free charger anywhere.

Returns:
    list[ChargeOption]: The options, marked as estimates; empty when no
        shuttle is low.
"""
def charge_options(model: EnergyModel, snapshot: FullSnapshot, charge_s: float = 1200.0,
                   *, depot_distances_m: dict[str, float] | None = None,
                   depot_speed_mps: float | None = None,
                   parking_distances_m: dict[tuple[str, str], float] | None = None,
                   depot_choices: dict[str, str] | None = None) -> list[ChargeOption]:
    if parking_distances_m is not None:
        if depot_distances_m is None or depot_speed_mps is None or depot_speed_mps <= 0:
            raise ValueError("Parking estimates require depot distances and a positive depot speed")
        for distance in parking_distances_m.values():
            require_non_negative("parking_distance_m", distance)
    vehicles = {v.vehicle_id: v for v in snapshot.vehicles}
    serving = {v.vehicle_id for v in snapshot.vehicles if v.service_state in IN_SERVICE_STATES}
    routes = {r.route_id: r for r in snapshot.routes}
    expires_at_tick = snapshot.tick + OPTION_LIFETIME_TICKS
    on_charger = {vehicle_id for c in snapshot.chargers for vehicle_id in c.occupied_vehicle_ids}
    offered = {o.option_id: o for o in snapshot.charge_options}
    decision_started = {}  # vehicle ID -> tick its current battery decision was first offered
    for o in snapshot.charge_options:
        started = o.offered_at_tick if o.offered_at_tick is not None else o.expires_at_tick - OPTION_LIFETIME_TICKS
        decision_started[o.vehicle_id] = min(started, decision_started.get(o.vehicle_id, started))
    options = []
    depot_chargers = {d.depot_id: set(d.charger_ids) for d in snapshot.depots}
    for forecast in energy_forecasts(model, snapshot, depot_distances_m=depot_distances_m, depot_speed_mps=depot_speed_mps,
                                     depot_choices=depot_choices):
        if forecast.loops_remaining is None or forecast.loops_remaining > LOW_LOOPS:
            continue
        vehicle = vehicles[forecast.vehicle_id]
        if (vehicle.vehicle_id in on_charger or
                not transition_allowed("vehicle", vehicle.service_state, "RETURNING_TO_DEPOT")):
            continue
        if forecast.loops_remaining >= snapshot.charge_decision_deferrals.get(vehicle.vehicle_id, LOW_LOOPS + 1):
            continue  # the operator chose CONTINUE; ask again once another loop is used
        prefix = f"charge-option-{vehicle.vehicle_id}"
        offered_at_tick = decision_started.get(vehicle.vehicle_id, snapshot.tick)
        if vehicle.vehicle_id in serving:  # only a serving shuttle can stay in service
            options.append(ChargeOption(
                option_id=f"{prefix}-continue",
                vehicle_id=vehicle.vehicle_id,
                revision=1,
                action="CONTINUE",
                validation="PASS",
                expires_at_tick=expires_at_tick,
                offered_at_tick=offered_at_tick,
                dependency_revisions={vehicle.vehicle_id: vehicle.aggregate_revision},
                out_of_service_s=0.0,
                battery_after_kwh=vehicle.battery_kwh,
                loops_after=forecast.loops_remaining,
                assumptions=[f"Reserve breached after {forecast.loops_remaining} more loops"],
                min_in_service_vehicles=len(serving),
            ))
        # Only a charger at the depot the distance was measured to; any charger without a choice
        chosen = depot_chargers.get((depot_choices or {}).get(vehicle.vehicle_id))
        charger = next((c for c in sorted(snapshot.chargers, key=lambda c: c.charger_id)
                        if len(c.occupied_vehicle_ids) < c.port_count and (chosen is None or c.charger_id in chosen)), None)
        if charger is not None:
            route = routes[vehicle.route_id]
            loop = loop_energy_kwh(model, route.distance_m, route.estimated_duration_s)
            travel_s = (forecast.next_depot_eta_s or 0.0) if depot_distances_m is not None else 0.0
            parking_m = (parking_distances_m or {}).get((vehicle.vehicle_id, charger.charger_id), 0.0)
            parking_s = parking_m / depot_speed_mps if parking_m else 0.0
            arrival = vehicle.battery_kwh
            if depot_distances_m is not None:
                arrival, _ = use_energy(model, arrival, depot_distances_m[vehicle.vehicle_id], travel_s)
            after = charge_battery_kwh(model, arrival, vehicle.battery_capacity_kwh,
                                       charger.power_kw, charge_s)
            if depot_distances_m is not None:
                after, _ = use_energy(model, after, parking_m, charge_s + parking_s)
            options.append(ChargeOption(
                option_id=f"{prefix}-{charger.charger_id}",
                vehicle_id=vehicle.vehicle_id,
                revision=1,
                action="CHARGE_NOW",
                charger_id=charger.charger_id,
                validation="FAIL" if depot_distances_m is not None and not forecast.reaches_depot_above_reserve else "PASS",
                expires_at_tick=expires_at_tick,
                offered_at_tick=offered_at_tick,
                dependency_revisions={vehicle.vehicle_id: vehicle.aggregate_revision,
                                      charger.charger_id: charger.aggregate_revision},
                out_of_service_s=travel_s + charge_s + parking_s,
                battery_after_kwh=after,
                loops_after=loops_remaining(model, after, vehicle.battery_capacity_kwh, loop),
                assumptions=([f"{charger.charger_id} is reserved from approval through arrival and charging",
                              f"Return estimate follows {depot_distances_m[vehicle.vehicle_id]:.1f} m of admitted path; excludes dwell and traffic",
                              f"Charge for {charge_s:g} s; energy-r1 auxiliary use is included",
                              f"Then drive {parking_m:.1f} m to standby parking before releasing the port"]
                             if depot_distances_m is not None
                             else [f"{charger.charger_id} stays free for {int(charge_s // 60)} minutes"]),
                min_in_service_vehicles=len(serving - {vehicle.vehicle_id}),
            ))
    return [_keep_or_revise(option, offered.get(option.option_id),
                            snapshot.charge_option_revisions.get(option.option_id, 0),
                            vehicles[option.vehicle_id], snapshot.tick)
            for option in options]

"""
# -----------------------------------------------------------------------------------
# Function: vehicle_option_ids
# -----------------------------------------------------------------------------------
Description:
    This function returns every option ID charge_options can issue for one
    shuttle in this snapshot (its CONTINUE option and a CHARGE_NOW option
    per charger), so callers can match a shuttle's options exactly instead
    of by ID prefix.

Args:
    snapshot (FullSnapshot): The training snapshot (for its chargers).
    vehicle_id (str): The shuttle.

Returns:
    set[str]: The option IDs.
"""
def vehicle_option_ids(snapshot: FullSnapshot, vehicle_id: str) -> set[str]:
    prefix = f"charge-option-{vehicle_id}"
    return {f"{prefix}-continue"} | {f"{prefix}-{c.charger_id}" for c in snapshot.chargers}

"""
# -----------------------------------------------------------------------------------
# Function: _keep_or_revise
# -----------------------------------------------------------------------------------
Description:
    This function keeps an existing offer frozen while it is unexpired,
    depends on the same revisions, and is still true: an offer without a
    charger cannot promise more than the shuttle's current (draining)
    battery. Otherwise it returns the new option with the revision after the
    last one issued for its ID, so a withdrawn and re-created offer never
    reuses a revision.

Args:
    option (ChargeOption): The freshly computed option (revision 1).
    previous (ChargeOption | None): The active offer with the same ID, if any.
    last_issued (int): The last revision ever issued for this ID (0 if none).
    vehicle (FleetVehicle): The shuttle the option is for, as it is now.
    tick (int): The snapshot tick.

Returns:
    ChargeOption: The offer to publish.
"""
def _keep_or_revise(option: ChargeOption, previous: ChargeOption | None, last_issued: int,
                    vehicle: FleetVehicle, tick: int) -> ChargeOption:
    if previous is not None:
        drained_below = previous.charger_id is None and previous.battery_after_kwh > vehicle.battery_kwh
        if (tick <= previous.expires_at_tick and previous.dependency_revisions == option.dependency_revisions
                and not drained_below
                and previous.min_in_service_vehicles == option.min_in_service_vehicles):
            return previous
        last_issued = max(last_issued, previous.revision)
    return option.model_copy(update={"revision": last_issued + 1})

"""
# -----------------------------------------------------------------------------------
# Function: with_battery_decisions
# -----------------------------------------------------------------------------------
Description:
    This function returns a copy of the snapshot with energy_forecasts,
    charge_options and charge_option_revisions filled in. A CONTINUE
    deferral is dropped once it no longer applies: the shuttle has fallen
    below the recorded loops (it has just been asked again), recovered above
    LOW_LOOPS, lost its forecast, or taken a charger port. It is the one
    call the session actor makes for each snapshot it commits; recording
    every issued revision here keeps the history even after an offer is
    withdrawn.

Args:
    model (EnergyModel): The energy parameters.
    snapshot (FullSnapshot): The snapshot to fill.
    charge_s (float): How long CHARGE_NOW keeps the shuttle on the charger,
        in seconds.

Returns:
    FullSnapshot: The filled copy; the input is unchanged.
"""
def with_battery_decisions(model: EnergyModel, snapshot: FullSnapshot, charge_s: float = 1200.0,
                           *, depot_distances_m: dict[str, float] | None = None,
                           depot_speed_mps: float | None = None,
                           parking_distances_m: dict[tuple[str, str], float] | None = None,
                           depot_choices: dict[str, str] | None = None) -> FullSnapshot:
    result = snapshot.model_copy(deep=True)
    result.energy_forecasts = energy_forecasts(model, snapshot, depot_distances_m=depot_distances_m, depot_speed_mps=depot_speed_mps,
                                               depot_choices=depot_choices)
    result.charge_options = charge_options(model, snapshot, charge_s, depot_distances_m=depot_distances_m,
                                          depot_speed_mps=depot_speed_mps, parking_distances_m=parking_distances_m,
                                          depot_choices=depot_choices)
    result.charge_option_revisions = {**snapshot.charge_option_revisions,
                                      **{o.option_id: o.revision for o in result.charge_options}}
    loops = {f.vehicle_id: f.loops_remaining for f in result.energy_forecasts}
    on_charger = {vehicle_id for c in snapshot.chargers for vehicle_id in c.occupied_vehicle_ids}
    result.charge_decision_deferrals = {
        vehicle_id: deferred for vehicle_id, deferred in snapshot.charge_decision_deferrals.items()
        if vehicle_id not in on_charger and loops.get(vehicle_id) is not None
        and deferred <= loops[vehicle_id] <= LOW_LOOPS}
    return result
