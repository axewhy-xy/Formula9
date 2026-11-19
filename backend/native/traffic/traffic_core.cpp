// The background traffic of src/traffic/city_traffic.py, natively.
//
// This is the same driving, step for step and rounding for rounding, as the Python CityTraffic: the same
// session from the same state drives to bit-identical state(), so a checkpoint written by either engine
// restores in the other, and a staged session's hash does not depend on which one ran it. The Python
// module stays the reference and the fallback (src/traffic/native_traffic.py chooses between them);
// tests/test_native_traffic.py holds the two to each other.
//
// Every number is a double rounded as CPython rounds it: the same operations in the same order, no
// contraction into fused multiply-adds, and CPython's own algorithms where its float arithmetic is not the
// C library's (math.hypot, float %, float ** 2, sum, math.atan2's special cases). Change the driving in
// city_traffic.py and here together.
//
// What a session calls every tick (step, plan_shuttle, place_shuttle, body_blocker) runs without the GIL,
// so several sessions in one process tick on several cores at once. Everything a Traffic holds belongs to
// the one session that owns it; the only state shared between sessions is a Network, which never changes
// except its junction-conflict cache, which is locked.

#include <nanobind/nanobind.h>
#include <nanobind/stl/pair.h>
#include <nanobind/stl/shared_ptr.h>
#include <nanobind/stl/string.h>
#include <nanobind/stl/tuple.h>
#include <nanobind/stl/unique_ptr.h>
#include <nanobind/stl/vector.h>

#include <algorithm>
#include <array>
#include <cfloat>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <stdexcept>
#include <string>
#include <tuple>
#include <unordered_map>
#include <utility>
#include <vector>

namespace nb = nanobind;
using namespace nb::literals;

#ifndef F9_TRAFFIC_SOURCE_HASH
#define F9_TRAFFIC_SOURCE_HASH "unknown"
#endif

namespace {

constexpr double INF = std::numeric_limits<double>::infinity();

// ---------------------------------------------------------------- constants, as city_traffic.py and
// road_network.py define them (checked against them on import: native_traffic.check_constants)

constexpr double PLAN_AHEAD_M = 80.0;
constexpr int MAX_PLANNED_ROADS = 8;
constexpr double REACTION_S = 1.0, COMFORT_BRAKE = 3.5, HARD_BRAKE = 9.0;
constexpr double YELLOW_BRAKE = 6.0;
constexpr double MIN_GAP_M = 2.0, STOP_MARGIN_M = 0.5, NODE_STOP_BACK_M = 3.0;
constexpr double PAST_LINE_M = 1.5;
constexpr double CLAIM_MARGIN_M = 8.0, NOTICE_S = 5.0;
constexpr double CLEARED_JUNCTION_M = 2.0;
constexpr double WAIT_PER_RANK_S = 5.0;
constexpr double STANDING_MPS = 2.0, STOPPED_MPS = 0.3;
constexpr double RELEASE_CLAIM_S = 3.0;
constexpr double STUCK_S = 90.0;
constexpr double ENTER_MPS = 5.0;
constexpr double SHUTTLE_SIGHT_M = 60.0, SHUTTLE_CLEARANCE_M = 0.3, SHUTTLE_BESIDE_M = 0.15, SHUTTLE_MOVING_MPS = 1.0;
constexpr double SHUTTLE_PASS_BEHIND_M = 8.0, SHUTTLE_PASS_AHEAD_M = 30.0;
constexpr double MAX_ASIDE_M = 3.5, ASIDE_MPS = 1.5, PASSING_MPS = 5.0, SHUTTLE_JUNCTION_M = 25.0,
                 STANDING_SHUTTLE_JUNCTION_M = 8.0;
constexpr double SHUTTLE_LANE_MPS = 1.0;
constexpr double SHUTTLE_LOOK_M = 60.0, SHUTTLE_LOOK_STEP_M = 2.0;
constexpr double SHUTTLE_SIDE_ROOM_M = 0.3, SHUTTLE_ONCOMING_ROOM_M = -0.1;
constexpr double SHUTTLE_ROAD_ALIGN = 0.7;
constexpr double SHUTTLE_FACING_SPAN_M = 4.0;
constexpr double SHUTTLE_TAKE_TURN_S = 4.0, SHUTTLE_GIVE_UP_S = 30.0;
constexpr double SHUTTLE_CROSS_NOTICE_M = 40.0, SHUTTLE_CROSS_NOTICE_S = 4.0;
constexpr double SHUTTLE_JUMP_M = 5.0;
constexpr double RUN_INTO_M = 16.0;
constexpr double SHUTTLE_BODY_REACH_M = 20.0;
constexpr double SHUTTLE_FOLLOW_EXTRA_M = 4.0, SHUTTLE_SWING_M = 2.0;
constexpr double TOUCH_M = 0.1;
constexpr double NO_LIMIT = 1e9;
constexpr double PIN_PICK_M = 30.0, PIN_TOUCH_M = 0.3;
constexpr double PINNED_PASS_MPS = 2.5;
constexpr double LANE_WIDTH_M = 3.2;
constexpr double MOVEMENT_REACH_M = 6.0, CONFLICT_M = 2.2;
constexpr double CELL_M = 40.0, MATCH_M = 5.0;
// signal_cycle: a junction's two phases of green, yellow and all-red
constexpr int GREEN_S = 30, YELLOW_S = 3, ALL_RED_S = 2, PHASES = 2;
constexpr int PHASE_S = GREEN_S + YELLOW_S + ALL_RED_S, CYCLE_S = PHASES * PHASE_S;
constexpr int AMBER_S = GREEN_S + YELLOW_S;

// ---------------------------------------------------------------- CPython's float arithmetic

using Binary = double (*)(double, double);
using Unary = double (*)(double);
// Called through these, so the compiler calls the C runtime's functions as CPython does rather than
// substituting expansions of its own
Binary volatile crt_pow = static_cast<Binary>(&::pow);
Binary volatile crt_atan2 = static_cast<Binary>(&::atan2);
Unary volatile crt_acos = static_cast<Unary>(&::acos);

constexpr double PY_PI = 3.14159265358979323846;
constexpr double RAD_TO_DEG = 180.0 / PY_PI;

// x ** 2 for a float: float_pow, which leaves zero, one and the sign to itself and the rest to pow
double py_square(double x) {
    if (x == 0.0) return 0.0;
    if (x < 0.0) x = -x;
    if (x == 1.0) return 1.0;
    return crt_pow(x, 2.0);
}

// a % b for floats: the result takes the divisor's sign
double py_mod(double a, double b) {
    double mod = std::fmod(a, b);
    if (mod != 0.0) {
        if ((b < 0) != (mod < 0)) mod += b;
    } else {
        mod = std::copysign(0.0, b);
    }
    return mod;
}

struct DoubleLength {
    double hi, lo;
};

DoubleLength dl_fast_sum(double a, double b) {
    double x = a + b;
    double y = (a - x) + b;
    return {x, y};
}

DoubleLength dl_mul(double x, double y) {
    double z = x * y;
    double zz = std::fma(x, y, -z);
    return {z, zz};
}

// math.hypot and math.dist of two coordinates: CPython's vector_norm, correctly rounded where the C
// library's hypot need not be
double vector_norm2(double a, double b, double max, bool found_nan) {
    if (std::isinf(max)) return max;
    if (found_nan) return std::numeric_limits<double>::quiet_NaN();
    if (max == 0.0) return max;
    int max_e;
    std::frexp(max, &max_e);
    if (max_e < -1023) return DBL_MIN * vector_norm2(a / DBL_MIN, b / DBL_MIN, max / DBL_MIN, found_nan);
    double scale = std::ldexp(1.0, -max_e);
    double csum = 1.0, frac1 = 0.0, frac2 = 0.0;
    for (double x : {a, b}) {
        x *= scale;
        DoubleLength pr = dl_mul(x, x);
        DoubleLength sm = dl_fast_sum(csum, pr.hi);
        csum = sm.hi;
        frac1 += pr.lo;
        frac2 += sm.lo;
    }
    double h = std::sqrt(csum - 1.0 + (frac1 + frac2));
    DoubleLength pr = dl_mul(-h, h);
    DoubleLength sm = dl_fast_sum(csum, pr.hi);
    csum = sm.hi;
    frac1 += pr.lo;
    frac2 += sm.lo;
    double x = csum - 1.0 + (frac1 + frac2);
    h += x / (2.0 * h);
    return h / scale;
}

double py_hypot(double x, double y) {
    x = std::fabs(x);
    y = std::fabs(y);
    double max = 0.0;
    bool found_nan = false;
    for (double v : {x, y}) {
        found_nan |= std::isnan(v);
        if (v > max) max = v;
    }
    return vector_norm2(x, y, max, found_nan);
}

double py_atan2(double y, double x) {
    if (std::isnan(x) || std::isnan(y)) return std::numeric_limits<double>::quiet_NaN();
    if (std::isinf(y)) {
        if (std::isinf(x)) return std::copysign(std::copysign(1.0, x) == 1.0 ? 0.25 * PY_PI : 0.75 * PY_PI, y);
        return std::copysign(0.5 * PY_PI, y);
    }
    if (std::isinf(x) || y == 0.0) return std::copysign(1.0, x) == 1.0 ? std::copysign(0.0, y) : std::copysign(PY_PI, y);
    return crt_atan2(y, x);
}

// sum() of floats since CPython 3.12: Neumaier's compensated summation
double py_sum(const std::vector<double>& values) {
    if (values.empty()) return 0.0;
    double total = values[0], c = 0.0;
    for (size_t i = 1; i < values.size(); ++i) {
        double x = values[i];
        double t = total + x;
        if (std::fabs(total) >= std::fabs(x))
            c += (total - t) + x;
        else
            c += (x - t) + total;
        total = t;
    }
    if (c != 0.0 && std::isfinite(c)) total += c;
    return total;
}

double copysign_towards(double max_delta, double direction) { return std::copysign(max_delta, direction); }

// ---------------------------------------------------------------- city_traffic's small functions

double krauss(double speed, double gap, double leader_speed, double reaction = REACTION_S) {
    if (gap <= 0) return 0.0;
    return std::max(0.0, leader_speed + (gap - leader_speed * reaction) / ((speed + leader_speed) / (2 * COMFORT_BRAKE) + reaction));
}

// 0 green, 1 yellow, 2 red; faulted, red
int light(double time_s, int offset_s, int phase, bool faulted = false) {
    if (faulted) return 2;
    double into = py_mod(time_s + offset_s - phase * PHASE_S, CYCLE_S);
    return into < GREEN_S ? 0 : into < AMBER_S ? 1 : 2;
}

uint32_t rng_step(uint32_t state) { return static_cast<uint32_t>((1664525ULL * state + 1013904223ULL) & 0xFFFFFFFFULL); }

double random01(uint32_t state) { return static_cast<double>(rng_step(state) & 0x00FFFFFFu) / 16777215.0; }

double move_towards(double current, double target, double max_delta) {
    if (std::fabs(target - current) <= max_delta) return target;
    return current + copysign_towards(max_delta, target - current);
}

bool overlap(double ax, double ay, double afx, double afy, double a_half_length, double a_half_width, double bx, double by,
             double bfx, double bfy, double b_half_length, double b_half_width) {
    const double axes[4][2] = {{afx, afy}, {-afy, afx}, {bfx, bfy}, {-bfy, bfx}};
    for (const auto& axis : axes) {
        double px = axis[0], py = axis[1];
        double a_reach = a_half_length * std::fabs(afx * px + afy * py) + a_half_width * std::fabs(-afy * px + afx * py);
        double b_reach = b_half_length * std::fabs(bfx * px + bfy * py) + b_half_width * std::fabs(-bfy * px + bfx * py);
        if (std::fabs((bx - ax) * px + (by - ay) * py) > a_reach + b_reach - TOUCH_M) return false;
    }
    return true;
}

double inverse_lerp(double a, double b, double value) {
    return a != b ? std::min(1.0, std::max(0.0, (value - a) / (b - a))) : 0.0;
}

double lerp(double a, double b, double t) { return a + (b - a) * std::min(1.0, std::max(0.0, t)); }

double angle_deg(double ax, double ay, double bx, double by) {
    double scale = std::sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
    if (scale < 1e-15) return 0.0;
    return RAD_TO_DEG * crt_acos(std::min(1.0, std::max(-1.0, (ax * bx + ay * by) / scale)));
}

struct Vec2 {
    double x, y;
};

Vec2 unit(double x, double y) {
    double length = py_hypot(x, y);
    if (length * length < 0.001) return {0.0, 1.0};
    return {x / length, y / length};
}

double point_to_segment(double px, double py, double ax, double ay, double bx, double by) {
    double dx = bx - ax, dy = by - ay;
    double length_sq = dx * dx + dy * dy;
    double share = length_sq > 0 ? std::min(1.0, std::max(0.0, ((px - ax) * dx + (py - ay) * dy) / length_sq)) : 0.0;
    return py_hypot(px - ax - dx * share, py - ay - dy * share);
}

double side_of(Vec2 p, Vec2 q, Vec2 r) { return (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x); }

double segment_distance(Vec2 a1, Vec2 a2, Vec2 b1, Vec2 b2) {
    if (side_of(a1, a2, b1) * side_of(a1, a2, b2) < 0 && side_of(b1, b2, a1) * side_of(b1, b2, a2) < 0) return 0.0;
    return std::min(std::min(std::min(point_to_segment(a1.x, a1.y, b1.x, b1.y, b2.x, b2.y),
                                      point_to_segment(a2.x, a2.y, b1.x, b1.y, b2.x, b2.y)),
                             point_to_segment(b1.x, b1.y, a1.x, a1.y, a2.x, a2.y)),
                    point_to_segment(b2.x, b2.y, a1.x, a1.y, a2.x, a2.y));
}

// ---------------------------------------------------------------- the network

struct Pose {
    double x, y, fx, fy;
};

struct Stop {
    double at;
    int offset, phase, junction;
};

struct Road {
    int index = 0;
    int road_id = 0;  // interned: only ever compared
    int lanes = 1;
    double speed = 0.0;
    bool oneway = false, service = false;
    int from_node = 0, to_node = 0;  // dense node numbers
    std::vector<double> xs, ys, along;
    double length = 0.0, sx = 0.0, sy = 1.0, ex = 0.0, ey = 1.0;
    std::vector<Stop> stops;
    double rank = 0.0, weight = 1.0;

    int lane_from(int lane) const { return std::min(std::max(lane, 0), lanes - 1); }

    double lane_offset(int lane) const {
        lane = lane_from(lane);
        return oneway ? (lane - (lanes - 1) * 0.5) * LANE_WIDTH_M : (lane + 0.5) * LANE_WIDTH_M;
    }

    Pose sample(double distance, double left) const {
        double d = std::min(std::max(distance, 0.0), length);
        size_t n = along.size();
        size_t index = static_cast<size_t>(std::lower_bound(along.begin(), along.end(), d) - along.begin());
        index = std::min(std::max(index, static_cast<size_t>(1)), n - 1);
        double ax = xs[index - 1], ay = ys[index - 1], bx = xs[index], by = ys[index];
        double segment = std::max(0.001, along[index] - along[index - 1]);
        double t = std::min(1.0, std::max(0.0, (d - along[index - 1]) / segment));
        Vec2 tangent = unit(bx - ax, by - ay);
        return {ax + (bx - ax) * t - tangent.y * left, ay + (by - ay) * t + tangent.x * left, tangent.x, tangent.y};
    }

    // How far a point is from the centreline, how far along it is, and the way the road runs there
    std::tuple<double, double, double, double> project(double px, double py) const {
        double best = INF, best_along = 0.0, tx = sx, ty = sy;
        for (size_t i = 1; i < xs.size(); ++i) {
            double ax = xs[i - 1], ay = ys[i - 1];
            double dx = xs[i] - ax, dy = ys[i] - ay;
            double length_sq = dx * dx + dy * dy;
            double share = length_sq > 0 ? std::min(1.0, std::max(0.0, ((px - ax) * dx + (py - ay) * dy) / length_sq)) : 0.0;
            double distance = py_hypot(px - ax - dx * share, py - ay - dy * share);
            if (distance >= best) continue;
            best = distance;
            best_along = along[i - 1] + (along[i] - along[i - 1]) * share;
            Vec2 tangent = unit(dx, dy);
            tx = tangent.x;
            ty = tangent.y;
        }
        return {best, best_along, tx, ty};
    }
};

struct Movement {
    int road, lane, next, next_lane;
    bool operator==(const Movement& o) const { return road == o.road && lane == o.lane && next == o.next && next_lane == o.next_lane; }
    bool operator<=(const Movement& o) const {
        return std::tie(road, lane, next, next_lane) <= std::tie(o.road, o.lane, o.next, o.next_lane);
    }
};

struct PairHash {
    size_t operator()(const std::pair<int64_t, int64_t>& key) const {
        uint64_t h = static_cast<uint64_t>(key.first) * 0x9E3779B97F4A7C15ULL;
        h ^= static_cast<uint64_t>(key.second) + 0x9E3779B97F4A7C15ULL + (h << 6) + (h >> 2);
        return static_cast<size_t>(h);
    }
};

struct ConflictKeyHash {
    size_t operator()(const std::pair<Movement, Movement>& key) const {
        uint64_t h = 1469598103934665603ULL;
        for (int v : {key.first.road, key.first.lane, key.first.next, key.first.next_lane, key.second.road, key.second.lane,
                      key.second.next, key.second.next_lane}) {
            h ^= static_cast<uint64_t>(static_cast<uint32_t>(v));
            h *= 1099511628211ULL;
        }
        return static_cast<size_t>(h);
    }
};

struct ConflictKeyEq {
    bool operator()(const std::pair<Movement, Movement>& a, const std::pair<Movement, Movement>& b) const {
        return a.first == b.first && a.second == b.second;
    }
};

class Network {
public:
    std::string area;
    std::vector<Road> roads;
    std::vector<int64_t> node_ids;
    std::unordered_map<int64_t, int> node_index;
    std::vector<std::vector<int>> exits;  // by dense node: the roads leaving it, in the Python network's order
    std::vector<int> entries, large_entries;
    std::unordered_map<std::pair<int64_t, int64_t>, std::vector<int>, PairHash> grid;
    std::vector<std::string> junctions;
    std::unordered_map<std::string, int> junction_index;

    int node(int64_t id) {
        auto found = node_index.find(id);
        if (found != node_index.end()) return found->second;
        int index = static_cast<int>(node_ids.size());
        node_ids.push_back(id);
        node_index.emplace(id, index);
        exits.emplace_back();
        return index;
    }

    const std::vector<int>& near(double x, double y) const {
        static const std::vector<int> none;
        auto found = grid.find({static_cast<int64_t>(std::floor(x / CELL_M)), static_cast<int64_t>(std::floor(y / CELL_M))});
        return found == grid.end() ? none : found->second;
    }

    // The road a vehicle at a point heading this way is on (-1 off the roads), and how far to the left of
    // the point its kerb lane's middle is
    std::pair<int, double> road_under(double x, double y, double hx, double hy, double align) const {
        int best = -1;
        double best_along = 0.0, best_distance = MATCH_M;
        for (int index : near(x, y)) {
            auto [distance, along, tx, ty] = roads[index].project(x, y);
            if (distance > best_distance || tx * hx + ty * hy < align) continue;
            best = index;
            best_along = along;
            best_distance = distance;
        }
        if (best < 0) return {-1, 0.0};
        const Road& road = roads[best];
        Pose kerb = road.sample(best_along, road.lane_offset(road.lanes - 1));
        return {best, (kerb.x - x) * -hy + (kerb.y - y) * hx};
    }

    std::pair<double, double> turn(const Road& road, const Road& next) const {
        double degrees = angle_deg(road.ex, road.ey, next.sx, next.sy);
        return {inverse_lerp(180.0, 0.0, degrees), inverse_lerp(120.0, 15.0, degrees)};
    }

    std::array<Vec2, 4> movement(const Movement& m) const {
        const Road& road = roads[m.road];
        const Road& next = roads[m.next];
        double offset = road.lane_offset(m.lane), next_offset = next.lane_offset(m.next_lane);
        Pose a = road.sample(road.length - MOVEMENT_REACH_M, offset), b = road.sample(road.length, offset);
        Pose c = next.sample(0.0, next_offset), d = next.sample(MOVEMENT_REACH_M, next_offset);
        return {Vec2{a.x, a.y}, Vec2{b.x, b.y}, Vec2{c.x, c.y}, Vec2{d.x, d.y}};
    }

    bool conflict(const Movement& a, const Movement& b) const {
        if (a.road == b.road && a.lane == b.lane) return false;
        if (a.next == b.next && a.next_lane == b.next_lane) return true;
        std::pair<Movement, Movement> key = a <= b ? std::make_pair(a, b) : std::make_pair(b, a);
        {
            std::shared_lock<std::shared_mutex> reading(conflicts_lock_);
            auto found = conflicts_.find(key);
            if (found != conflicts_.end()) return found->second;
        }
        auto first = movement(key.first), second = movement(key.second);
        bool crosses = false;
        for (int i = 0; i < 3 && !crosses; ++i)
            for (int j = 0; j < 3 && !crosses; ++j)
                crosses = segment_distance(first[i], first[i + 1], second[j], second[j + 1]) < CONFLICT_M;
        std::unique_lock<std::shared_mutex> writing(conflicts_lock_);
        conflicts_.emplace(key, crosses);
        return crosses;
    }

private:
    mutable std::shared_mutex conflicts_lock_;
    mutable std::unordered_map<std::pair<Movement, Movement>, bool, ConflictKeyHash, ConflictKeyEq> conflicts_;
};

// ---------------------------------------------------------------- a shuttle's route

class Route {
public:
    std::vector<Vec2> points;
    std::vector<double> along;
    double length = 0.0;
    bool closed = false;
    std::vector<std::pair<double, double>> bends;
    std::string sig;

    Vec2 point(double at) const {
        if (length <= 0) return points[0];
        at = closed ? py_mod(at, length) : std::min(std::max(at, 0.0), length);
        size_t index = static_cast<size_t>(std::lower_bound(along.begin(), along.end(), at) - along.begin());
        if (index < along.size() && along[index] == at) return points[index];
        index = std::min(std::max(index, static_cast<size_t>(1)), points.size() - 1);
        double start = along[index - 1];
        double t = (at - start) / (along[index] - start);
        Vec2 a = points[index - 1], b = points[index];
        return {a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t};
    }

    Vec2 direction(double at) const {
        double behind, ahead;
        if (closed) {
            behind = at - SHUTTLE_FACING_SPAN_M;
            ahead = at + SHUTTLE_FACING_SPAN_M;
        } else {
            behind = std::max(0.0, at - SHUTTLE_FACING_SPAN_M);
            ahead = std::min(length, at + SHUTTLE_FACING_SPAN_M);
        }
        Vec2 a = point(behind), b = point(ahead);
        double span = py_hypot(b.x - a.x, b.y - a.y);
        if (span > 1e-3) return {(b.x - a.x) / span, (b.y - a.y) / span};
        a = points[0];
        b = points[1];
        span = py_hypot(b.x - a.x, b.y - a.y);
        return span > 0 ? Vec2{(b.x - a.x) / span, (b.y - a.y) / span} : Vec2{0.0, 1.0};
    }

    double ahead(double at, double from) const {
        double result = at - from;
        if (closed && result < -length / 2) result += length;
        return result;
    }

    Vec2 reach(double at) const {
        if (closed || (0.0 <= at && at <= length)) return point(at);
        Vec2 p, h;
        double over;
        if (at < 0) {
            p = points[0];
            h = direction(0.0);
            over = at;
        } else {
            p = points.back();
            h = direction(length);
            over = at - length;
        }
        return {p.x + h.x * over, p.y + h.y * over};
    }

    Pose body(double at, double aside, double half_length) const {
        Vec2 h = direction(at + half_length);
        Vec2 f = reach(at + half_length);
        double fx = f.x - h.y * aside, fy = f.y + h.x * aside;
        h = direction(at - half_length);
        Vec2 b = reach(at - half_length);
        double bx = b.x - h.y * aside, by = b.y + h.x * aside;
        double span = py_hypot(fx - bx, fy - by);
        if (span < 1e-6) {
            h = direction(at);
            Vec2 p = point(at);
            return {p.x - h.y * aside, p.y + h.x * aside, h.x, h.y};
        }
        return {(fx + bx) / 2, (fy + by) / 2, (fx - bx) / span, (fy - by) / span};
    }
};

using RoutePtr = std::shared_ptr<const Route>;

// ---------------------------------------------------------------- the traffic

struct Claim {
    uint64_t uid;
    int node, from_r, from_lane, to_r, to_lane;
    bool entered;
};

// A junction's record of a claim: who holds it and the movement it makes
struct ClaimRef {
    uint64_t uid;
    int vehicle;
    Movement movement;
    int to_r;
};

struct Vehicle {
    int slot = 0, seed = 0;
    double length = 0.0, half = 0.0, half_width = 0.0, accel = 0.0;
    bool large = false;
    double speed_factor = 1.0;
    uint32_t rng = 1;
    int road = 0, lane = 0, lane_index = 0;
    double distance = 0.0, speed = 0.0, target_speed = 0.0, stopped_s = 0.0, wait_s = 0.0, aside = 0.0, target_aside = 0.0;
    bool outside = false;
    std::vector<int> ahead;
    std::vector<Claim> claims;
    double x = 0.0, y = 0.0, fx = 0.0, fy = 0.0;
    bool placed = false;

    bool has_claim(int to) const {
        for (const Claim& claim : claims)
            if (claim.to_r == to && !claim.entered) return true;
        return false;
    }

    bool has_claim_from(int road_index) const {
        for (const Claim& claim : claims)
            if (claim.from_r == road_index && !claim.entered) return true;
        return false;
    }
};

// What never changes about the sample's vehicles
struct Sample {
    std::vector<std::string> ids, models;
};

// The vehicles and their junction claims, which a staged copy shares until either changes them
struct Fleet {
    std::vector<Vehicle> vehicles;
    std::vector<std::vector<ClaimRef>> claims_by_node;
    uint64_t next_claim = 1;
};

struct Shuttle {
    std::string id;
    double x = 0.0, y = 0.0, fx = 0.0, fy = 1.0, half_length = 0.0, half_width = 0.0, speed = 0.0;
    bool driven = false;
    std::vector<int> gives_way_to;  // a set, kept sorted
    double wait_s = 0.0, stops_within_m = NO_LIMIT;
    bool in_traffic = false;
    double aside = 0.0, target_aside = 0.0;
    bool placed = false;
    RoutePtr route;
    std::string route_sig;
    double along = 0.0;

    bool gives_way(int slot) const { return std::binary_search(gives_way_to.begin(), gives_way_to.end(), slot); }
    void give_way(int slot) {
        auto at = std::lower_bound(gives_way_to.begin(), gives_way_to.end(), slot);
        if (at == gives_way_to.end() || *at != slot) gives_way_to.insert(at, slot);
    }
};

struct Plan {
    double limit_mps = INF, cap_m = INF, bend_mps = INF;
    bool held = false;
};

struct Span {
    double start, end;
};

// The hazards, replaced whole: in the order they were found, and by road
struct Hazards {
    std::vector<std::pair<int, std::vector<Span>>> items;
    std::vector<int> by_road;  // index into items, or -1
};

struct Pin {
    std::string key;
    std::vector<int> slots;
    double until;
};

struct Fault {
    std::string junction;
    double until;
};

struct Request {
    double rank, to_node;
    int vehicle, origin, lane, to, to_lane;
    bool can_take;
    int plan_index;
};

struct Body {
    double x, y, fx, fy, half_length, half_width;
};

struct Look {
    bool found;
    double along, side, tx, ty;
};

// What blocks a shuttle's body: nothing (0), a vehicle (1) or another shuttle (2), and which
struct Blocker {
    int kind = 0, index = -1;
};

class Traffic {
public:
    explicit Traffic(std::shared_ptr<const Network> network)
        : net_(std::move(network)), sample_(std::make_shared<Sample>()), fleet_(std::make_shared<Fleet>()) {
        fleet_->claims_by_node.resize(net_->node_ids.size());
        hazards_ = empty_hazards();
        fault_until_.assign(net_->junctions.size(), std::numeric_limits<double>::quiet_NaN());
    }

    // ------------------------------------------------ building and copying

    void add_vehicle(int seed, const std::string& id, const std::string& model, double length, double half, double half_width,
                     bool large, double accel, double speed_factor, uint32_t rng, int road, int lane, double distance, double speed) {
        own_vehicles();
        if (sample_.use_count() > 1) sample_ = std::make_shared<Sample>(*sample_);
        Vehicle vehicle;
        vehicle.slot = static_cast<int>(fleet_->vehicles.size());
        vehicle.seed = seed;
        vehicle.length = length;
        vehicle.half = half;
        vehicle.half_width = half_width;
        vehicle.large = large;
        vehicle.accel = accel;
        vehicle.speed_factor = speed_factor;
        vehicle.rng = rng;
        vehicle.road = road_checked(road);
        vehicle.lane = lane;
        vehicle.distance = distance;
        vehicle.speed = speed;
        fleet_->vehicles.push_back(std::move(vehicle));
        auto sample = std::const_pointer_cast<Sample>(sample_);
        sample->ids.push_back(id);
        sample->models.push_back(model);
        pinned_.push_back(0);
    }

    void pose_all() {
        own_vehicles();
        for (Vehicle& vehicle : fleet_->vehicles) pose(vehicle);
    }

    // A staged session's copy: it shares the vehicles until either changes them (city_traffic's
    // __deepcopy__ and _own_vehicles)
    std::unique_ptr<Traffic> copy() {
        auto twin = std::unique_ptr<Traffic>(new Traffic(*this));
        owns_ = twin->owns_ = false;
        return twin;
    }

    // ------------------------------------------------ hazards and world events

    void set_hazards(const std::vector<std::pair<int, std::vector<std::pair<double, double>>>>& found, bool replan = true) {
        own_vehicles();
        auto hazards = std::make_shared<Hazards>();
        hazards->by_road.assign(net_->roads.size(), -1);
        for (const auto& [road, spans] : found) {
            road_checked(road);
            std::vector<Span> converted;
            for (const auto& [start, end] : spans) converted.push_back({start, end});
            if (hazards->by_road[road] >= 0) {
                hazards->items[hazards->by_road[road]].second = std::move(converted);
            } else {
                hazards->by_road[road] = static_cast<int>(hazards->items.size());
                hazards->items.emplace_back(road, std::move(converted));
            }
        }
        hazards_ = hazards;
        if (!replan) return;
        for (Vehicle& vehicle : fleet_->vehicles) {
            for (size_t i = 0; i < vehicle.ahead.size(); ++i) {
                int road = vehicle.ahead[i];
                if (hazards_->by_road[road] >= 0 && !vehicle.has_claim(road)) {
                    vehicle.ahead.resize(i);
                    break;
                }
            }
        }
    }

    bool pin_vehicle(const std::string& key, int road_index, double distance, int lane, double until_s, int count) {
        if (find_pin(key) >= 0) return true;
        if (until_s <= time_s || count < 1) return false;
        own_vehicles();
        fill_lanes();
        const Road& road = net_->roads[road_checked(road_index)];
        lane = road.lane_from(lane);
        std::vector<int> queue = lane_of(road.index, lane);
        auto& vehicles = fleet_->vehicles;
        std::vector<int> chosen;
        std::vector<double> places;
        double front = std::min(std::max(distance, 0.0), road.length);
        for (int n = 0; n < count; ++n) {
            auto taken = [&](int slot) {
                return pinned_[slot] || std::find(chosen.begin(), chosen.end(), slot) != chosen.end();
            };
            int pick = -1;
            double pick_off = 0.0;
            for (int slot : queue) {
                const Vehicle& v = vehicles[slot];
                if (taken(slot) || !(std::fabs(v.distance - front) <= PIN_PICK_M)) continue;
                double off = std::fabs(v.distance - front);
                if (pick < 0 || off < pick_off || (off == pick_off && slot < pick)) {
                    pick = slot;
                    pick_off = off;
                }
            }
            if (pick < 0) {
                for (const Vehicle& v : vehicles)
                    if (v.outside && !taken(v.slot)) {
                        pick = v.slot;
                        break;
                    }
            }
            if (pick < 0) {
                for (const Vehicle& v : vehicles)
                    if (!taken(v.slot)) {
                        pick = v.slot;
                        break;
                    }
            }
            if (pick < 0) return false;
            const Vehicle& picked = vehicles[pick];
            if (!chosen.empty()) front -= vehicles[chosen.back()].half + PIN_TOUCH_M + picked.half;
            double at = std::min(std::max(front, picked.half), road.length);
            Pose place = road.sample(at, road.lane_offset(lane));
            for (const Shuttle& s : shuttles_) {
                if (s.placed && overlap(s.x, s.y, s.fx, s.fy, s.half_length + MIN_GAP_M, s.half_width, place.x, place.y, place.fx,
                                        place.fy, picked.half, picked.half_width))
                    return false;
            }
            chosen.push_back(pick);
            places.push_back(at);
        }
        for (size_t i = 0; i < chosen.size(); ++i) {
            Vehicle& v = vehicles[chosen[i]];
            release_all_claims(v);
            v.ahead.clear();
            v.outside = false;
            v.road = road.index;
            v.lane = lane;
            v.distance = places[i];
            v.speed = v.target_speed = v.stopped_s = v.wait_s = 0.0;
            v.aside = v.target_aside = 0.0;
            pose(v);
        }
        pins_.push_back({key, chosen, until_s});
        for (int slot : chosen) pinned_[slot] = 1;
        for (Vehicle& v : vehicles) {
            if (!v.placed || pinned_[v.slot]) continue;
            for (int slot : chosen) {
                const Vehicle& p = vehicles[slot];
                if (std::fabs(v.x - p.x) < SHUTTLE_BODY_REACH_M && std::fabs(v.y - p.y) < SHUTTLE_BODY_REACH_M &&
                    overlap(p.x, p.y, p.fx, p.fy, p.half + MIN_GAP_M / 2, p.half_width, v.x, v.y, v.fx, v.fy, v.half, v.half_width)) {
                    enter(v);
                    pose(v);
                    break;
                }
            }
        }
        return true;
    }

    void pin_until(const std::string& key, double until_s) {
        int index = find_pin(key);
        if (index >= 0 && pins_[index].until != until_s) pins_[index].until = until_s;
    }

    void set_signal_fault(const std::string& junction, double until_s) {
        if (!(until_s > time_s)) return;
        for (Fault& fault : faults_)
            if (fault.junction == junction) {
                fault.until = until_s;
                index_faults();
                return;
            }
        faults_.push_back({junction, until_s});
        index_faults();
    }

    // ------------------------------------------------ driving

    void step(double dt, double time) {
        own_vehicles();
        time_s = time;
        bodies_.clear();
        for (const Shuttle& s : shuttles_)
            if (s.placed) bodies_.push_back({s.x, s.y, s.fx, s.fy, s.half_length, s.half_width});
        fill_lanes();
        if (!pins_.empty() || !faults_.empty()) clear_events();
        auto& vehicles = fleet_->vehicles;
        for (Vehicle& v : vehicles)
            if (v.outside || (caught(v) && !pinned_[v.slot])) enter(v);
        clear_shuttle_jams();
        fill_lanes();
        for (Vehicle& v : vehicles) {
            if (v.outside || pinned_[v.slot]) continue;
            release_claims(v);
            plan_ahead(v);
            ask_for_junction(v);
        }
        answer_junctions(dt);
        for (Vehicle& v : vehicles)
            if (!v.outside) v.target_speed = pinned_[v.slot] ? 0.0 : safe_speed(v);
        for (Vehicle& v : vehicles) {
            if (v.outside) continue;
            if (pinned_[v.slot]) {
                v.speed = 0.0;
                continue;
            }
            move(v, dt);
        }
        bodies_.clear();
        for (Vehicle& v : vehicles) pose(v);
    }

    // ------------------------------------------------ shuttles

    Shuttle& shuttle(const std::string& id, double half_length, double half_width) {
        int index = find_shuttle(id);
        if (index >= 0) return shuttles_[index];
        Shuttle s;
        s.id = id;
        s.half_length = half_length;
        s.half_width = half_width;
        shuttles_.push_back(std::move(s));
        return shuttles_.back();
    }

    Shuttle& shuttle_named(const std::string& id) {
        int index = find_shuttle(id);
        if (index < 0) throw nb::key_error(id.c_str());
        return shuttles_[index];
    }

    void forget_shuttle(const std::string& id) {
        int index = find_shuttle(id);
        if (index >= 0) shuttles_.erase(shuttles_.begin() + index);
    }

    Plan plan_shuttle(Shuttle& shuttle, const Route& route, double along, double cruise) {
        double speed = shuttle.speed;
        Vec2 heading = route.direction(along);
        Vec2 p = route.point(along);
        shuttle.target_aside = net_->road_under(p.x, p.y, heading.x, heading.y, SHUTTLE_ROAD_ALIGN).second;
        Plan plan;
        for (const auto& [at, degrees] : route.bends) {
            double to_bend = route.ahead(at, along);
            if (0 <= to_bend && to_bend <= PLAN_AHEAD_M) {
                double turn = lerp(3.0, cruise, inverse_lerp(120.0, 15.0, degrees));
                plan.bend_mps = std::min(plan.bend_mps, std::sqrt(turn * turn + 2 * COMFORT_BRAKE * to_bend));
            }
        }
        shuttle.gives_way_to.clear();
        double look = std::max(SHUTTLE_LOOK_M, speed * speed / (2 * COMFORT_BRAKE) + speed * REACTION_S + CLAIM_MARGIN_M);
        if (!route.closed) look = std::min(look, route.length - along);
        look_.clear();
        double d = 0.0;
        while (true) {
            Vec2 h = route.direction(along + d);
            Vec2 q = route.point(along + d);
            look_.push_back({q.x - h.y * shuttle.aside, q.y + h.x * shuttle.aside});
            if (d >= look) break;
            d = std::min(d + SHUTTLE_LOOK_STEP_M, look);
        }
        double stops_within = NO_LIMIT;
        if (look_.size() >= 2) {
            double reach = look + SHUTTLE_LOOK_STEP_M + shuttle.half_length;
            double ox = shuttle.x, oy = shuttle.y;
            for (const Vehicle& v : fleet_->vehicles) {
                if (!v.placed) continue;
                double ax = v.x, ay = v.y;
                if (py_square(ax - ox) + py_square(ay - oy) > reach * reach) continue;
                Look seen = along_look(ax, ay);
                if (!seen.found) continue;
                double along_m = seen.along, side = seen.side, tx = seen.tx, ty = seen.ty;
                double facing_x = v.fx, facing_y = v.fy;
                double alike = facing_x * tx + facing_y * ty;
                if (std::fabs(alike) <= 0.5 && !v.claims.empty() && v.speed > STANDING_MPS &&
                    (along_m <= 0 || side >= shuttle.half_width + v.half + SHUTTLE_SIDE_ROOM_M)) {
                    auto crossing = crosses_plan(shuttle, v);
                    along_m = crossing.along;
                    side = crossing.side;
                    tx = crossing.tx;
                    ty = crossing.ty;
                    facing_x = crossing.fx;
                    facing_y = crossing.fy;
                    alike = facing_x * tx + facing_y * ty;
                }
                if (along_m <= 0) continue;
                double across = std::fabs(facing_x * ty - facing_y * tx);
                if (pinned_[v.slot] &&
                    side >= (shuttle.half_width + across * v.half + std::fabs(alike) * v.half_width + SHUTTLE_ONCOMING_ROOM_M)) {
                    double to_it = std::max(0.0, along_m - v.half - shuttle.half_length);
                    plan.limit_mps = std::min(plan.limit_mps, std::sqrt(PINNED_PASS_MPS * PINNED_PASS_MPS + 2 * COMFORT_BRAKE * to_it));
                    continue;
                }
                double room = alike < -0.5 ? SHUTTLE_ONCOMING_ROOM_M : SHUTTLE_SIDE_ROOM_M;
                if (side >= shuttle.half_width + across * v.half + std::fabs(alike) * v.half_width + room) continue;
                if (runs_into(v, shuttle)) continue;
                double gap;
                if (!gap_to(shuttle, plan, along_m, side, tx, ty, facing_x, facing_y, alike, v.half, v.half_width, v.speed,
                               cruise, gap))
                    continue;
                if (std::max(0.0, v.speed * alike) >= STANDING_MPS) continue;
                shuttle.give_way(v.slot);
                stops_within = std::min(stops_within, std::max(0.0, gap));
            }
            for (const Shuttle& other : shuttles_) {
                if (&other == &shuttle || !other.placed) continue;
                double ax = other.x, ay = other.y;
                if (py_square(ax - ox) + py_square(ay - oy) > py_square(reach + SHUTTLE_CROSS_NOTICE_M)) continue;
                Look seen = along_look(ax, ay);
                if (!seen.found) continue;
                double along_m = seen.along, side = seen.side, tx = seen.tx, ty = seen.ty;
                double alike = other.fx * tx + other.fy * ty;
                if (alike <= 0.5 && other.driven) {
                    bool first = shuttle.id < other.id;
                    if (first && (other.speed < SHUTTLE_MOVING_MPS || along_m <= 0 ||
                                  side >= shuttle.half_width + other.half_length + SHUTTLE_SIDE_ROOM_M))
                        continue;
                    if (!first) {
                        auto crossing = crosses_look(shuttle, ax, ay, other.fx, other.fy, other.speed, other.half_length, other.half_width);
                        if (!crossing.found) continue;
                        along_m = crossing.along;
                        side = crossing.side;
                        tx = crossing.tx;
                        ty = crossing.ty;
                    }
                    alike = other.fx * tx + other.fy * ty;
                }
                if (along_m <= 0) continue;
                double gap;
                if (gap_to(shuttle, plan, along_m - SHUTTLE_FOLLOW_EXTRA_M, side, tx, ty, other.fx, other.fy, alike, other.half_length,
                              other.half_width, other.speed, cruise, gap) &&
                    std::max(0.0, other.speed * alike) < STANDING_MPS)
                    stops_within = std::min(stops_within, std::max(0.0, gap));
            }
        }
        shuttle.stops_within_m = stops_within;
        return plan;
    }

    bool something_alongside(const Shuttle& shuttle, double shift) const {
        if (std::fabs(shift) < 0.01) return false;
        double fx = shuttle.fx, fy = shuttle.fy;
        double lx = -fy, ly = fx;
        double reach = shuttle.half_length * 2 + MIN_GAP_M;
        for (const Vehicle& v : fleet_->vehicles) {
            if (!v.placed) continue;
            double tx = v.x - shuttle.x, ty = v.y - shuttle.y;
            if (tx * tx + ty * ty > reach * reach) continue;
            double side = tx * lx + ty * ly;
            if (side * shift <= 0) continue;
            double across = std::fabs(v.fx * lx + v.fy * ly);
            double along = std::fabs(v.fx * fx + v.fy * fy);
            if (std::fabs(tx * fx + ty * fy) > shuttle.half_length + along * v.half + across * v.half_width + MIN_GAP_M) continue;
            if (std::fabs(side) <
                (shuttle.half_width + across * v.half + along * v.half_width + SHUTTLE_SIDE_ROOM_M + SHUTTLE_LANE_MPS))
                return true;
        }
        return false;
    }

    void place_shuttle(Shuttle& shuttle, const RoutePtr& route, double along, double speed, double dt, bool held_by_traffic,
                       bool held_by_light, bool in_traffic, double px, double py) {
        shuttle.speed = speed;
        shuttle.in_traffic = in_traffic;
        shuttle.wait_s = held_by_traffic && !held_by_light && speed < STOPPED_MPS ? shuttle.wait_s + dt : 0.0;
        if (!route) {
            shuttle.driven = false;
            shuttle.gives_way_to.clear();
            shuttle.x = px;
            shuttle.y = py;
            shuttle.route.reset();
            shuttle.route_sig.clear();
            shuttle.along = along;
            shuttle.placed = true;
            return;
        }
        bool jumped = !shuttle.placed || shuttle.route_sig != route->sig || along < shuttle.along - 1e-6 ||
                      along - shuttle.along > std::max(SHUTTLE_JUMP_M, speed * dt * 2);
        shuttle.driven = true;
        if (jumped) {
            Vec2 h = route->direction(along);
            Vec2 p = route->point(along);
            shuttle.target_aside = net_->road_under(p.x, p.y, h.x, h.y, SHUTTLE_ROAD_ALIGN).second;
            shuttle.aside = shuttle.target_aside;
            shuttle.gives_way_to.clear();
            shuttle.wait_s = 0.0;
        } else if (something_alongside(shuttle, shuttle.target_aside - shuttle.aside)) {
            shuttle.target_aside = shuttle.aside;
        }
        double aside = move_towards(shuttle.aside, shuttle.target_aside, SHUTTLE_LANE_MPS * dt);
        if (jumped || aside == shuttle.aside || !side_blocked(shuttle, *route, along, aside)) shuttle.aside = aside;
        Pose body = route->body(along, shuttle.aside, shuttle.half_length);
        shuttle.x = body.x;
        shuttle.y = body.y;
        shuttle.fx = body.fx;
        shuttle.fy = body.fy;
        shuttle.route = route;
        shuttle.route_sig = route->sig;
        shuttle.along = along;
        shuttle.placed = true;
    }

    Blocker body_blocker(const Shuttle& shuttle, const Route& route, double along) const {
        Pose at = route.body(along, shuttle.aside, shuttle.half_length);
        double reach = shuttle.half_length + 10.0;
        for (const Vehicle& v : fleet_->vehicles) {
            if (!v.placed || std::fabs(v.x - at.x) > reach || std::fabs(v.y - at.y) > reach) continue;
            if (overlap(at.x, at.y, at.fx, at.fy, shuttle.half_length, shuttle.half_width, v.x, v.y, v.fx, v.fy, v.half, v.half_width) &&
                !(shuttle.placed && overlap(shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length, shuttle.half_width, v.x, v.y,
                                            v.fx, v.fy, v.half, v.half_width)))
                return {1, v.slot};
        }
        for (size_t i = 0; i < shuttles_.size(); ++i) {
            const Shuttle& peer = shuttles_[i];
            if (&peer == &shuttle || !peer.placed || std::fabs(peer.x - at.x) > reach + peer.half_length ||
                std::fabs(peer.y - at.y) > reach + peer.half_length)
                continue;
            if (follows(shuttle, peer.x, peer.y, peer.fx, peer.fy)) continue;
            if (overlap(at.x, at.y, at.fx, at.fy, shuttle.half_length, shuttle.half_width, peer.x, peer.y, peer.fx, peer.fy, peer.half_length,
                        peer.half_width) &&
                !(shuttle.placed && overlap(shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length, shuttle.half_width, peer.x,
                                            peer.y, peer.fx, peer.fy, peer.half_length, peer.half_width)))
                return {2, static_cast<int>(i)};
        }
        return {};
    }

    void clear_shuttles() {
        own_vehicles();
        fill_lanes();
        bodies_.clear();
        for (const Shuttle& s : shuttles_)
            if (s.placed) bodies_.push_back({s.x, s.y, s.fx, s.fy, s.half_length, s.half_width});
        for (const Shuttle& s : shuttles_) {
            if (!s.placed) continue;
            for (Vehicle& v : fleet_->vehicles) {
                if (pinned_[v.slot]) continue;
                if (v.placed && overlap(s.x, s.y, s.fx, s.fy, s.half_length + MIN_GAP_M, s.half_width + SHUTTLE_SIDE_ROOM_M, v.x, v.y,
                                        v.fx, v.fy, v.half, v.half_width)) {
                    enter(v);
                    pose(v);
                }
            }
        }
        bodies_.clear();
    }

    // ------------------------------------------------ what is reported

    // The vehicles on the roads, packed as city_traffic.FRAME_RECORD after FRAME_RECORD, and how many
    std::pair<std::string, int> frame() const {
        std::string packed;
        int count = 0;
        for (const Vehicle& v : fleet_->vehicles) {
            if (v.outside) continue;
            uint16_t seed = static_cast<uint16_t>(v.seed);
            int32_t x = static_cast<int32_t>(clamp_int32(std::nearbyint(v.x * 100)));
            int32_t y = static_cast<int32_t>(clamp_int32(std::nearbyint(v.y * 100)));
            int16_t heading = static_cast<int16_t>(std::nearbyint(RAD_TO_DEG * py_atan2(v.fy, v.fx) * 10));
            uint16_t speed = static_cast<uint16_t>(std::min(std::nearbyint(v.speed * 100), 65535.0));
            char record[14];
            std::memcpy(record, &seed, 2);
            std::memcpy(record + 2, &x, 4);
            std::memcpy(record + 6, &y, 4);
            std::memcpy(record + 10, &heading, 2);
            std::memcpy(record + 12, &speed, 2);
            packed.append(record, 14);
            ++count;
        }
        return {packed, count};
    }

    std::tuple<int, int, int, int64_t, double> report() {
        own_vehicles();
        fill_lanes();
        int driving = 0, outside = 0, overlaps = 0;
        double speed = 0.0;
        for (const Vehicle& v : fleet_->vehicles) {
            if (v.outside) {
                ++outside;
                continue;
            }
            ++driving;
            speed += v.speed;
            const std::vector<int>& queue = lane_of(v.road, v.lane);
            if (static_cast<size_t>(v.lane_index + 1) < queue.size()) {
                const Vehicle& front = fleet_->vehicles[queue[v.lane_index + 1]];
                if (front.distance - front.half < v.distance + v.half) ++overlaps;
            }
        }
        return {driving, outside, overlaps, red_light_runs, driving ? speed / driving : 0.0};
    }

    std::vector<std::pair<std::string, std::string>> shuttle_overlaps() const {
        std::vector<std::pair<std::string, std::string>> found;
        std::vector<const Shuttle*> placed;
        for (const Shuttle& s : shuttles_)
            if (s.placed) placed.push_back(&s);
        for (size_t i = 0; i < placed.size(); ++i) {
            const Shuttle& s = *placed[i];
            for (size_t j = i + 1; j < placed.size(); ++j) {
                const Shuttle& o = *placed[j];
                if (overlap(s.x, s.y, s.fx, s.fy, s.half_length, s.half_width, o.x, o.y, o.fx, o.fy, o.half_length, o.half_width))
                    found.emplace_back(s.id, o.id);
            }
            for (const Vehicle& v : fleet_->vehicles) {
                if (v.placed && std::fabs(v.x - s.x) < 30 && std::fabs(v.y - s.y) < 30 &&
                    overlap(s.x, s.y, s.fx, s.fy, s.half_length, s.half_width, v.x, v.y, v.fx, v.fy, v.half, v.half_width))
                    found.emplace_back(s.id, sample_->ids[v.slot]);
            }
        }
        return found;
    }

    // ------------------------------------------------ for the Python side

    const Network& network() const { return *net_; }
    const std::vector<Vehicle>& vehicles() const { return fleet_->vehicles; }
    const Sample& sample() const { return *sample_; }
    std::vector<Shuttle>& shuttles() { return shuttles_; }
    const Hazards& hazards() const { return *hazards_; }
    const std::vector<Pin>& pins() const { return pins_; }
    const std::vector<Fault>& faults() const { return faults_; }
    const std::vector<char>& pinned() const { return pinned_; }

    Vehicle& vehicle_checked(int slot) {
        if (slot < 0 || static_cast<size_t>(slot) >= fleet_->vehicles.size()) throw nb::index_error();
        return fleet_->vehicles[slot];
    }

    int road_checked(int road) const {
        if (road < 0 || static_cast<size_t>(road) >= net_->roads.size()) throw nb::index_error("no such road");
        return road;
    }

    // A checkpoint, already read and checked by the Python side: vehicles as (road, lane, distance, speed,
    // stopped_s, wait_s, aside, outside, rng, ahead, claims as (node id, from, from lane, to, to lane,
    // entered)), in the sample's order
    using VehicleState = std::tuple<int, int, double, double, double, double, double, bool, uint32_t, std::vector<int>,
                                    std::vector<std::tuple<int64_t, int, int, int, int, bool>>>;

    void load(double time, int64_t runs, const std::vector<VehicleState>& vehicles) {
        own_vehicles();
        if (vehicles.size() != fleet_->vehicles.size()) throw std::invalid_argument("Traffic checkpoint does not match the area's sample");
        time_s = time;
        red_light_runs = runs;
        for (auto& claims : fleet_->claims_by_node) claims.clear();
        for (size_t i = 0; i < vehicles.size(); ++i) {
            const auto& [road, lane, distance, speed, stopped_s, wait_s, aside, outside, rng, ahead, claims] = vehicles[i];
            Vehicle& v = fleet_->vehicles[i];
            v.road = road_checked(road);
            if (lane < 0 || lane >= net_->roads[v.road].lanes) throw std::invalid_argument("Traffic checkpoint has a lane its road does not");
            v.lane = lane;
            v.distance = distance;
            v.speed = speed;
            v.stopped_s = stopped_s;
            v.wait_s = wait_s;
            v.aside = aside;
            v.outside = outside;
            v.rng = rng;
            v.ahead.clear();
            for (int r : ahead) v.ahead.push_back(road_checked(r));
            v.claims.clear();
            for (const auto& [node_id, from_r, from_lane, to_r, to_lane, entered] : claims) {
                auto found = net_->node_index.find(node_id);
                if (found == net_->node_index.end()) throw std::invalid_argument("Traffic checkpoint claims an unknown junction");
                add_claim(v, found->second, road_checked(from_r), from_lane, road_checked(to_r), to_lane, entered);
            }
            pose(v);
        }
    }

    void load_events(const std::vector<std::tuple<std::string, std::vector<int>, double>>& pins,
                     const std::vector<std::pair<std::string, double>>& faults) {
        pins_.clear();
        std::fill(pinned_.begin(), pinned_.end(), 0);
        for (const auto& [key, slots, until] : pins) {
            for (int slot : slots)
                if (slot < 0 || static_cast<size_t>(slot) >= pinned_.size())
                    throw std::invalid_argument("Traffic checkpoint pins a vehicle it does not have");
            int index = find_pin(key);
            if (index >= 0)
                pins_[index] = {key, slots, until};
            else
                pins_.push_back({key, slots, until});
            for (int slot : slots) pinned_[slot] = 1;
        }
        faults_.clear();
        for (const auto& [junction, until] : faults) {
            bool known = false;
            for (Fault& fault : faults_)
                if (fault.junction == junction) {
                    fault.until = until;
                    known = true;
                }
            if (!known) faults_.push_back({junction, until});
        }
        index_faults();
    }

    double time_s = 0.0;
    int64_t red_light_runs = 0;

private:
    Traffic(const Traffic& other)
        : time_s(other.time_s),
          red_light_runs(other.red_light_runs),
          net_(other.net_),
          sample_(other.sample_),
          fleet_(other.fleet_),
          owns_(false),
          shuttles_(other.shuttles_),
          hazards_(other.hazards_),
          pins_(other.pins_),
          pinned_(other.pinned_),
          faults_(other.faults_),
          fault_until_(other.fault_until_) {}

    std::shared_ptr<const Hazards> empty_hazards() const {
        auto hazards = std::make_shared<Hazards>();
        hazards->by_road.assign(net_->roads.size(), -1);
        return hazards;
    }

    int find_pin(const std::string& key) const {
        for (size_t i = 0; i < pins_.size(); ++i)
            if (pins_[i].key == key) return static_cast<int>(i);
        return -1;
    }

    int find_shuttle(const std::string& id) const {
        for (size_t i = 0; i < shuttles_.size(); ++i)
            if (shuttles_[i].id == id) return static_cast<int>(i);
        return -1;
    }

    void index_faults() {
        std::fill(fault_until_.begin(), fault_until_.end(), std::numeric_limits<double>::quiet_NaN());
        for (const Fault& fault : faults_) {
            auto found = net_->junction_index.find(fault.junction);
            if (found != net_->junction_index.end()) fault_until_[found->second] = fault.until;
        }
    }

    // Whether a junction's lights are stuck now (city_traffic's _fault)
    bool faulted(int junction) const {
        if (faults_.empty()) return false;
        double until = fault_until_[junction];
        return !std::isnan(until) && time_s < until;
    }

    static double clamp_int32(double value) { return std::min(std::max(value, -2147483647.0), 2147483647.0); }

    void own_vehicles() {
        if (owns_) return;
        fleet_ = std::make_shared<Fleet>(*fleet_);
        reset_lanes();
        owns_ = true;
    }

    void add_claim(Vehicle& v, int node, int from_r, int from_lane, int to_r, int to_lane, bool entered) {
        uint64_t uid = fleet_->next_claim++;
        v.claims.push_back({uid, node, from_r, from_lane, to_r, to_lane, entered});
        fleet_->claims_by_node[node].push_back({uid, v.slot, Movement{from_r, from_lane, to_r, to_lane}, to_r});
    }

    void unlist_claim(const Claim& claim) {
        auto& listed = fleet_->claims_by_node[claim.node];
        for (auto it = listed.begin(); it != listed.end(); ++it)
            if (it->uid == claim.uid) {
                listed.erase(it);
                return;
            }
    }

    const std::vector<Span>* spans_on(int road) const {
        int index = hazards_->by_road[road];
        return index < 0 ? nullptr : &hazards_->items[index].second;
    }

    bool has_hazard(int road) const { return hazards_->by_road[road] >= 0; }

    void pose(Vehicle& v) const {
        if (v.outside) {
            v.placed = false;
            return;
        }
        const Road& road = net_->roads[v.road];
        Pose p = road.sample(v.distance, road.lane_offset(v.lane) + v.aside);
        v.x = p.x;
        v.y = p.y;
        v.fx = p.fx;
        v.fy = p.fy;
        v.placed = true;
    }

    std::vector<int>& lane_of(int road, int lane) {
        if (lanes_.size() < net_->roads.size()) {
            lanes_.resize(net_->roads.size());
            filled_flag_.resize(net_->roads.size(), 0);
        }
        auto& lanes = lanes_[road];
        if (lanes.empty()) lanes.resize(net_->roads[road].lanes);
        return lanes[lane];
    }

    void reset_lanes() {
        for (int road : filled_) {
            for (auto& lane : lanes_[road]) lane.clear();
            filled_flag_[road] = 0;
        }
        filled_.clear();
    }

    void mark_filled(int road) {
        if (!filled_flag_[road]) {
            filled_flag_[road] = 1;
            filled_.push_back(road);
        }
    }

    // Each lane's vehicles, back to front
    void fill_lanes() {
        reset_lanes();
        auto& vehicles = fleet_->vehicles;
        for (Vehicle& v : vehicles) {
            if (v.outside) continue;
            std::vector<int>& lane = lane_of(v.road, v.lane);
            mark_filled(v.road);
            lane.push_back(v.slot);
        }
        const auto& ids = sample_->ids;
        for (int road : filled_) {
            for (auto& lane : lanes_[road]) {
                if (lane.size() > 1)
                    std::stable_sort(lane.begin(), lane.end(), [&](int a, int b) {
                        const Vehicle &va = vehicles[a], &vb = vehicles[b];
                        if (va.distance != vb.distance) return va.distance < vb.distance;
                        return ids[a] < ids[b];
                    });
                for (size_t i = 0; i < lane.size(); ++i) vehicles[lane[i]].lane_index = static_cast<int>(i);
            }
        }
    }

    bool caught(const Vehicle& v) const {
        const std::vector<Span>* spans = spans_on(v.road);
        if (!spans) return false;
        for (const Span& span : *spans)
            if (v.distance + v.half > span.start + STOP_MARGIN_M / 2 && v.distance - v.half < span.end) return true;
        return false;
    }

    void clear_events() {
        auto& vehicles = fleet_->vehicles;
        std::vector<Pin> due;
        for (const Pin& pin : pins_)
            if (pin.until <= time_s) due.push_back(pin);
        for (const Pin& pin : due) {
            pins_.erase(pins_.begin() + find_pin(pin.key));
            for (int slot : pin.slots) {
                pinned_[slot] = 0;
                Vehicle& v = vehicles[slot];
                if (!v.outside) enter(v);
            }
        }
        size_t before = faults_.size();
        faults_.erase(std::remove_if(faults_.begin(), faults_.end(), [&](const Fault& fault) { return fault.until <= time_s; }),
                      faults_.end());
        if (faults_.size() != before) index_faults();
    }

    void plan_ahead(Vehicle& v) {
        const auto& roads = net_->roads;
        double reach = roads[v.road].length - v.distance;
        for (int road : v.ahead) reach += roads[road].length;
        while (v.ahead.empty() || (reach < PLAN_AHEAD_M && static_cast<int>(v.ahead.size()) < MAX_PLANNED_ROADS)) {
            int next = choose_next_road(v, v.ahead.empty() ? v.road : v.ahead.back());
            if (next < 0) break;
            v.ahead.push_back(next);
            reach += roads[next].length;
        }
    }

    int choose_next_road(Vehicle& v, int origin_index) {
        const auto& roads = net_->roads;
        const Road& origin = roads[origin_index];
        const auto& exits = net_->exits[origin.to_node];
        if (exits.empty()) return -1;
        std::vector<int> allowed;
        for (int road : exits)
            if (!(v.large && roads[road].service)) allowed.push_back(road);
        std::vector<int> clear;
        for (int road : allowed)
            if (!has_hazard(road)) clear.push_back(road);
        if (!clear.empty()) allowed = clear;
        std::vector<int> candidates;
        for (int road : allowed)
            if (roads[road].road_id != origin.road_id) candidates.push_back(road);
        if (candidates.empty()) candidates = allowed;
        if (candidates.empty()) return -1;
        if (candidates.size() == 1) return candidates[0];
        std::vector<double> weights;
        for (int road : candidates)
            weights.push_back(std::max(0.01, roads[road].weight * lerp(0.4, 1.6, net_->turn(origin, roads[road]).first)));
        v.rng = rng_step(v.rng);
        double pick = random01(v.rng) * py_sum(weights);
        for (size_t i = 0; i < candidates.size(); ++i) {
            pick -= weights[i];
            if (pick <= 0) return candidates[i];
        }
        return candidates.back();
    }

    void release_claims(Vehicle& v) {
        auto& claims = v.claims;
        for (int i = static_cast<int>(claims.size()) - 1; i >= 0; --i) {
            const Claim& claim = claims[i];
            bool cleared = claim.entered && (v.road != claim.to_r || v.distance - v.half > CLEARED_JUNCTION_M);
            if (!cleared && (claim.entered || v.stopped_s < RELEASE_CLAIM_S)) continue;
            unlist_claim(claim);
            claims.erase(claims.begin() + i);
        }
    }

    void release_all_claims(Vehicle& v) {
        for (const Claim& claim : v.claims) unlist_claim(claim);
        v.claims.clear();
    }

    bool must_stop(const Stop& stop, double front_to_line, double speed) const {
        if (front_to_line < -PAST_LINE_M) return false;
        int colour = light(time_s, stop.offset, stop.phase, faulted(stop.junction));
        if (colour == 0) return false;
        return colour == 2 || speed * speed / (2 * YELLOW_BRAKE) <= front_to_line;
    }

    bool light_holds(const Vehicle& v, const Road& road, double road_start) const {
        for (const Stop& stop : road.stops)
            if (must_stop(stop, road_start + stop.at - v.half, v.speed)) return true;
        return false;
    }

    void ask_for_junction(Vehicle& v) {
        const auto& roads = net_->roads;
        int road = v.road, lane = v.lane;
        double road_start = -v.distance;
        double speed = v.speed;
        double reach = speed * speed / (2 * COMFORT_BRAKE) + speed * REACTION_S + CLAIM_MARGIN_M;
        double notice = std::max(reach, speed * NOTICE_S);
        for (size_t i = 0; i < v.ahead.size(); ++i) {
            const Road& here = roads[road];
            int next = v.ahead[i];
            if (!here.stops.empty() && light_holds(v, here, road_start)) return;
            int next_lane = roads[next].lane_from(lane);
            double to_node = road_start + here.length;
            if (to_node - v.half > notice) return;
            if (!v.has_claim(next)) {
                if (i == 0) {
                    const std::vector<int>& queue = lane_of(road, lane);
                    if (static_cast<size_t>(v.lane_index + 1) < queue.size() &&
                        !fleet_->vehicles[queue[v.lane_index + 1]].has_claim_from(road))
                        return;
                }
                int node = here.to_node;
                if (requests_.size() < net_->node_ids.size()) requests_.resize(net_->node_ids.size());
                auto& asked = requests_[node];
                if (asked.empty()) asked_.push_back(node);
                double rank = here.rank + v.wait_s / WAIT_PER_RANK_S;
                asked.push_back({rank, to_node, v.slot, road, lane, next, next_lane, to_node - v.half <= reach, static_cast<int>(i)});
                return;
            }
            road_start = to_node;
            road = next;
            lane = next_lane;
        }
    }

    void answer_junctions(double dt) {
        auto& vehicles = fleet_->vehicles;
        const auto& ids = sample_->ids;
        std::vector<Movement> denied;
        for (int node : asked_) {
            auto& asked = requests_[node];
            if (asked.size() > 1)
                std::stable_sort(asked.begin(), asked.end(), [&](const Request& a, const Request& b) {
                    if (-a.rank != -b.rank) return -a.rank < -b.rank;
                    if (a.to_node != b.to_node) return a.to_node < b.to_node;
                    return ids[a.vehicle] < ids[b.vehicle];
                });
            denied.clear();
            auto& claims = fleet_->claims_by_node[node];
            for (const Request& request : asked) {
                Vehicle& v = vehicles[request.vehicle];
                Movement movement{request.origin, request.lane, request.to, request.to_lane};
                bool crossed = false;
                for (const ClaimRef& claim : claims)
                    if (claim.vehicle != v.slot && net_->conflict(movement, claim.movement)) {
                        crossed = true;
                        break;
                    }
                if (!crossed)
                    for (const Movement& other : denied)
                        if (net_->conflict(movement, other)) {
                            crossed = true;
                            break;
                        }
                if (!crossed && request.can_take && !shuttle_holds_node(net_->roads[request.origin], v) &&
                    has_room_beyond(v, request.to, request.to_lane, request.plan_index)) {
                    add_claim(v, node, request.origin, request.lane, request.to, request.to_lane, false);
                    v.wait_s = 0.0;
                    continue;
                }
                if (request.can_take) v.wait_s += dt;
                if (crossed || !request.can_take) denied.push_back(movement);
            }
            asked.clear();
        }
        asked_.clear();
    }

    bool shuttle_holds_node(const Road& arriving, const Vehicle& v) const {
        if (shuttles_.empty()) return false;
        double nx = arriving.xs.back(), ny = arriving.ys.back();
        for (const Shuttle& s : shuttles_) {
            if (s.gives_way(v.slot)) continue;
            bool moving = s.speed > SHUTTLE_MOVING_MPS;
            double reach = moving ? SHUTTLE_JUNCTION_M : STANDING_SHUTTLE_JUNCTION_M;
            double tx = nx - s.x, ty = ny - s.y;
            if (tx * tx + ty * ty > reach * reach) continue;
            double ahead = tx * s.fx + ty * s.fy;
            if (s.driven && s.wait_s < SHUTTLE_TAKE_TURN_S && ahead - s.half_length > s.stops_within_m) continue;
            if (!moving || ahead > -s.half_length - CLEARED_JUNCTION_M) return true;
        }
        return false;
    }

    bool has_room_beyond(const Vehicle& v, int road, int lane, int index) {
        const auto& roads = net_->roads;
        double needed = v.length + MIN_GAP_M;
        double passed = 0.0;
        while (true) {
            if (const std::vector<Span>* spans = spans_on(road))
                for (const Span& span : *spans)
                    if (passed + span.start < needed) return false;
            for (int slot : lane_of(road, lane)) {
                if (slot == v.slot) continue;
                const Vehicle& other = fleet_->vehicles[slot];
                if (other.speed < STANDING_MPS && passed + other.distance - other.half < needed) return false;
                break;
            }
            passed += roads[road].length;
            index += 1;
            if (passed >= needed || static_cast<size_t>(index) >= v.ahead.size()) return true;
            road = v.ahead[index];
            lane = roads[road].lane_from(lane);
        }
    }

    double safe_speed(Vehicle& v) {
        const auto& roads = net_->roads;
        const auto& vehicles = fleet_->vehicles;
        double speed = v.speed;
        double limit = roads[v.road].speed * v.speed_factor;
        double half = v.half;
        int road = v.road, lane = v.lane;
        double road_start = -v.distance;
        size_t i = 0;
        while (true) {
            const Road& here = roads[road];
            const std::vector<int>& queue = lane_of(road, lane);
            size_t leader_index = i == 0 ? static_cast<size_t>(v.lane_index + 1) : 0;
            int leader = leader_index < queue.size() ? queue[leader_index] : -1;
            if (leader == v.slot) leader = leader_index + 1 < queue.size() ? queue[leader_index + 1] : -1;
            if (leader >= 0) {
                const Vehicle& l = vehicles[leader];
                limit = std::min(limit, krauss(speed, road_start + l.distance - l.half - half - MIN_GAP_M, l.speed));
            }
            if (const std::vector<Span>* spans = spans_on(road))
                for (const Span& span : *spans)
                    if (road_start + span.start > 0)
                        limit = std::min(limit, krauss(speed, road_start + span.start - half - STOP_MARGIN_M, 0.0));
            for (const Stop& stop : here.stops)
                if (must_stop(stop, road_start + stop.at - half, speed))
                    limit = std::min(limit, krauss(speed, road_start + stop.at - half - STOP_MARGIN_M, 0.0));
            if (i >= v.ahead.size()) break;
            int next = v.ahead[i];
            double to_node = road_start + here.length;
            if (!v.has_claim(next)) {
                limit = std::min(limit, krauss(speed, to_node - half - NODE_STOP_BACK_M, 0.0));
                break;
            }
            double full = here.speed * v.speed_factor;
            double turn = lerp(3.0, full, net_->turn(here, roads[next]).second);
            limit = std::min(limit, std::sqrt(turn * turn + 2 * COMFORT_BRAKE * std::max(0.0, to_node - half)));
            road_start = to_node;
            road = next;
            lane = roads[next].lane_from(lane);
            if (road_start > PLAN_AHEAD_M) break;
            i += 1;
        }
        if (shuttles_.empty()) {
            v.target_aside = 0.0;
            return limit;
        }
        return shuttle_limit(v, limit);
    }

    double shuttle_limit(Vehicle& v, double limit) const {
        v.target_aside = 0.0;
        if (!v.placed) return limit;
        double half = v.half;
        double fx = v.fx, fy = v.fy;
        double in_lane = std::max(0.0, LANE_WIDTH_M / 2 - v.half_width);
        for (const Shuttle& s : shuttles_) {
            if (s.gives_way(v.slot)) continue;
            double tx = s.x - v.x, ty = s.y - v.y;
            if (tx * tx + ty * ty > SHUTTLE_SIGHT_M * SHUTTLE_SIGHT_M) continue;
            double ahead = tx * fx + ty * fy;
            double side = tx * -fy + ty * fx;
            double clearance = s.half_width + v.half_width + SHUTTLE_CLEARANCE_M;
            double gap = ahead - s.half_length - half - MIN_GAP_M;
            double alike = s.fx * fx + s.fy * fy;
            bool moving = s.speed > SHUTTLE_MOVING_MPS;
            if (std::fabs(alike) < 0.5) {
                if (ahead > 0 && std::fabs(side) < clearance + s.half_length) limit = std::min(limit, krauss(v.speed, gap, 0.0));
                continue;
            }
            if ((moving || s.in_traffic) && alike > 0 && ahead > 0) {
                if (std::fabs(side) < clearance) limit = std::min(limit, krauss(v.speed, gap - SHUTTLE_SWING_M, s.speed * alike));
                continue;
            }
            if (s.driven && alike > 0 && ahead < 0) continue;
            if (ahead < -s.half_length - half - SHUTTLE_PASS_BEHIND_M || ahead > SHUTTLE_PASS_AHEAD_M) continue;
            if (moving && alike > 0 && s.speed <= v.speed) continue;
            double lane_side = side + v.aside;
            double aside = lane_side <= 0 ? std::max(0.0, clearance + lane_side) : -std::max(0.0, clearance - lane_side);
            if (aside == 0) continue;
            if (std::fabs(aside) > std::fabs(v.target_aside)) v.target_aside = std::min(std::max(aside, -MAX_ASIDE_M), MAX_ASIDE_M);
            if (ahead > 0 && std::fabs(aside) > in_lane) limit = std::min(limit, PASSING_MPS);
            if (ahead > 0 && std::fabs(side) < clearance - SHUTTLE_BESIDE_M) limit = std::min(limit, krauss(v.speed, gap, 0.0));
        }
        return limit;
    }

    void move(Vehicle& v, double dt) {
        const auto& roads = net_->roads;
        double rate = v.target_speed < v.speed ? HARD_BRAKE : v.accel;
        v.speed = move_towards(v.speed, v.target_speed, rate * dt);
        double aside = v.aside;
        v.aside = move_towards(v.aside, v.target_aside, ASIDE_MPS * dt);
        if (!bodies_.empty() && would_hit_shuttle(v, v.speed * dt)) {
            v.speed = 0.0;
            if (would_hit_shuttle(v, 0.0)) v.aside = aside;
        }
        v.stopped_s = v.speed < STOPPED_MPS ? v.stopped_s + dt : 0.0;
        if (v.stopped_s > STUCK_S) {
            enter(v);
            return;
        }
        double before = v.distance;
        v.distance += v.speed * dt;
        for (const Stop& stop : roads[v.road].stops) {
            if (before + v.half <= stop.at && stop.at < v.distance + v.half && light(time_s, stop.offset, stop.phase) == 2 &&
                !faulted(stop.junction))
                red_light_runs += 1;
        }
        int safety = 0;
        while (v.distance > roads[v.road].length && safety < 6) {
            safety += 1;
            if (v.ahead.empty()) {
                enter(v);
                return;
            }
            v.distance -= roads[v.road].length;
            int next = v.ahead.front();
            v.ahead.erase(v.ahead.begin());
            for (Claim& claim : v.claims)
                if (claim.to_r == next) claim.entered = true;
            v.road = next;
            v.lane = roads[next].lane_from(v.lane);
        }
    }

    bool would_hit_shuttle(const Vehicle& v, double step_m) const {
        double x = v.x, y = v.y;
        bool have_future = false;
        Pose future{};
        for (const Body& body : bodies_) {
            if (std::fabs(body.x - x) > SHUTTLE_BODY_REACH_M || std::fabs(body.y - y) > SHUTTLE_BODY_REACH_M) continue;
            if (!have_future) {
                future = this->future(v, step_m);
                have_future = true;
            }
            if (overlap(body.x, body.y, body.fx, body.fy, body.half_length, body.half_width, future.x, future.y, future.fx, future.fy,
                        v.half, v.half_width) &&
                !(v.placed && overlap(body.x, body.y, body.fx, body.fy, body.half_length, body.half_width, x, y, v.fx, v.fy, v.half,
                                      v.half_width)))
                return true;
        }
        return false;
    }

    void enter(Vehicle& v) {
        release_all_claims(v);
        v.ahead.clear();
        v.stopped_s = v.wait_s = v.aside = 0.0;
        v.placed = false;
        v.outside = true;
        const std::vector<int>* entries = v.large ? &net_->large_entries : &net_->entries;
        if (entries->empty()) entries = &net_->entries;
        if (entries->empty()) return;
        v.rng = rng_step(v.rng);
        size_t count = entries->size();
        for (size_t i = 0; i < std::min(count, static_cast<size_t>(8)); ++i) {
            const Road& road = net_->roads[(*entries)[((static_cast<uint64_t>(v.rng) + i) & 0xFFFFFFFFULL) % count]];
            if (has_hazard(road.index) || !room_at_start(road, road.lane_from(v.lane), v.length)) continue;
            v.outside = false;
            v.distance = 0.0;
            v.speed = road.stops.empty() ? std::min(v.speed, ENTER_MPS) : 0.0;
            v.road = road.index;
            v.lane = road.lane_from(v.lane);
            std::vector<int>& queue = lane_of(road.index, v.lane);
            mark_filled(road.index);
            queue.insert(queue.begin(), v.slot);
            return;
        }
    }

    bool room_at_start(const Road& road, int lane, double length) {
        for (const ClaimRef& claim : fleet_->claims_by_node[road.from_node])
            if (claim.to_r == road.index) return false;
        for (int slot : lane_of(road.index, lane)) {
            const Vehicle& other = fleet_->vehicles[slot];
            if (other.road == road.index && other.distance - other.half < length + MIN_GAP_M) return false;
        }
        if (!bodies_.empty()) {
            Pose p = road.sample(length / 2, road.lane_offset(lane));
            for (const Body& body : bodies_)
                if (std::fabs(body.x - p.x) < SHUTTLE_BODY_REACH_M && std::fabs(body.y - p.y) < SHUTTLE_BODY_REACH_M &&
                    overlap(body.x, body.y, body.fx, body.fy, body.half_length, body.half_width, p.x, p.y, p.fx, p.fy,
                            length / 2 + MIN_GAP_M, LANE_WIDTH_M / 2))
                    return false;
        }
        return true;
    }

    void clear_shuttle_jams() {
        auto& vehicles = fleet_->vehicles;
        for (Shuttle& s : shuttles_) {
            if (!s.driven || s.wait_s < SHUTTLE_GIVE_UP_S) continue;
            for (int slot : s.gives_way_to) {  // already sorted
                Vehicle& v = vehicles[slot];
                if (!v.outside && v.speed < STOPPED_MPS && !pinned_[slot]) enter(v);
            }
            for (int n = 0; n < 4; ++n) {
                if (!s.route) break;
                Blocker blocker = body_blocker(s, *s.route, s.along + SHUTTLE_LOOK_STEP_M);
                if (blocker.kind != 1) break;
                Vehicle& v = vehicles[blocker.index];
                if (v.speed >= STOPPED_MPS || pinned_[v.slot]) break;
                enter(v);
                pose(v);
            }
            s.gives_way_to.clear();
            s.wait_s = 0.0;
        }
    }

    bool gap_to(const Shuttle& shuttle, Plan& plan, double along_m, double side, double tx, double ty, double facing_x, double facing_y,
             double alike, double half_length, double half_width, double speed, double cruise, double& out) {
        double across = std::fabs(facing_x * ty - facing_y * tx);
        double side_reach = across * half_length + std::fabs(alike) * half_width;
        double room = alike < -0.5 ? SHUTTLE_ONCOMING_ROOM_M : SHUTTLE_SIDE_ROOM_M;
        if (side >= shuttle.half_width + side_reach + room) return false;
        double gap = along_m - std::fabs(alike) * half_length - across * half_width - shuttle.half_length - MIN_GAP_M;
        double limit = krauss(shuttle.speed, gap, std::max(0.0, speed * alike));
        if (limit < cruise) plan.held = true;
        plan.limit_mps = std::min(plan.limit_mps, limit);
        plan.cap_m = std::min(plan.cap_m, std::max(0.0, gap));
        out = gap;
        return true;
    }

    struct Crossing {
        bool found;
        double along, side, tx, ty, fx, fy;
    };

    Crossing crosses_look(const Shuttle& shuttle, double ax, double ay, double facing_x, double facing_y, double speed, double half_length,
                          double half_width) const {
        double notice = std::min(SHUTTLE_CROSS_NOTICE_M, speed * SHUTTLE_CROSS_NOTICE_S) + half_length;
        double d = 0.0;
        while (true) {
            Look seen = along_look(ax + facing_x * d, ay + facing_y * d);
            if (seen.found && seen.along > 0 && seen.side < shuttle.half_width + half_width + SHUTTLE_SIDE_ROOM_M)
                return {true, seen.along, seen.side, seen.tx, seen.ty, 0.0, 0.0};
            if (d >= notice) {
                Look here = along_look(ax, ay);
                return {false, here.along, here.side, here.tx, here.ty, 0.0, 0.0};
            }
            d = std::min(d + SHUTTLE_LOOK_STEP_M, notice);
        }
    }

    Crossing crosses_plan(const Shuttle& shuttle, const Vehicle& v) const {
        double notice = std::min(SHUTTLE_CROSS_NOTICE_M, v.speed * SHUTTLE_CROSS_NOTICE_S) + v.half;
        double d = 0.0;
        while (true) {
            Pose at = future(v, d);
            Look seen = along_look(at.x, at.y);
            if (seen.found && seen.along > 0 && seen.side < shuttle.half_width + v.half_width + SHUTTLE_SIDE_ROOM_M)
                return {true, seen.along, seen.side, seen.tx, seen.ty, at.fx, at.fy};
            if (d >= notice) {
                Look here = along_look(v.x, v.y);
                return {false, here.along, here.side, here.tx, here.ty, v.fx, v.fy};
            }
            d = std::min(d + SHUTTLE_LOOK_STEP_M, notice);
        }
    }

    bool runs_into(const Vehicle& v, const Shuttle& s) const {
        double d = SHUTTLE_LOOK_STEP_M;
        while (d <= RUN_INTO_M) {
            Pose at = future(v, d);
            if (overlap(s.x, s.y, s.fx, s.fy, s.half_length, s.half_width, at.x, at.y, at.fx, at.fy, v.half, v.half_width)) return true;
            d += SHUTTLE_LOOK_STEP_M;
        }
        return false;
    }

    // Where a vehicle's middle will be this much further along the roads it plans to take
    Pose future(const Vehicle& v, double ahead_m) const {
        const auto& roads = net_->roads;
        int road = v.road, lane = v.lane;
        double distance = v.distance + ahead_m;
        for (int next : v.ahead) {
            if (distance <= roads[road].length) break;
            distance -= roads[road].length;
            road = next;
            lane = roads[next].lane_from(lane);
        }
        const Road& here = roads[road];
        return here.sample(distance, here.lane_offset(lane) + v.aside);
    }

    Look along_look(double px, double py) const {
        double best_side = INF, best_along = 0.0, best_tx = 0.0, best_ty = 0.0;
        double travelled = 0.0;
        for (size_t i = 0; i + 1 < look_.size(); ++i) {
            double ax = look_[i].x, ay = look_[i].y;
            double dx = look_[i + 1].x - ax, dy = look_[i + 1].y - ay;
            double length = py_hypot(dx, dy);
            if (length <= 0) continue;
            double share = std::min(1.0, std::max(0.0, ((px - ax) * dx + (py - ay) * dy) / (length * length)));
            double distance = py_hypot(px - ax - dx * share, py - ay - dy * share);
            if (distance < best_side) {
                best_side = distance;
                best_along = travelled + length * share;
                best_tx = dx / length;
                best_ty = dy / length;
            }
            travelled += length;
        }
        return {best_side < INF, best_along, best_side, best_tx, best_ty};
    }

    static bool follows(const Shuttle& shuttle, double x, double y, double fx, double fy) {
        return (x - shuttle.x) * shuttle.fx + (y - shuttle.y) * shuttle.fy < 0 && fx * shuttle.fx + fy * shuttle.fy > 0.5;
    }

    bool side_blocked(const Shuttle& shuttle, const Route& route, double along, double aside) const {
        Pose at = route.body(along, aside, shuttle.half_length);
        for (const Vehicle& v : fleet_->vehicles) {
            if (!v.placed || std::fabs(v.x - at.x) > SHUTTLE_BODY_REACH_M || std::fabs(v.y - at.y) > SHUTTLE_BODY_REACH_M) continue;
            if (overlap(at.x, at.y, at.fx, at.fy, shuttle.half_length, shuttle.half_width, v.x, v.y, v.fx, v.fy, v.half, v.half_width) &&
                !overlap(shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length, shuttle.half_width, v.x, v.y, v.fx, v.fy, v.half,
                         v.half_width))
                return true;
        }
        return false;
    }

    std::shared_ptr<const Network> net_;
    std::shared_ptr<const Sample> sample_;
    std::shared_ptr<Fleet> fleet_;
    bool owns_ = true;
    std::vector<Shuttle> shuttles_;
    std::shared_ptr<const Hazards> hazards_;
    std::vector<Pin> pins_;
    std::vector<char> pinned_;
    std::vector<Fault> faults_;
    std::vector<double> fault_until_;
    // Working space for one step, never shared with a copy
    std::vector<std::vector<std::vector<int>>> lanes_;
    std::vector<int> filled_;
    std::vector<char> filled_flag_;
    std::vector<std::vector<Request>> requests_;
    std::vector<int> asked_;
    std::vector<Vec2> look_;
    std::vector<Body> bodies_;
};

// ---------------------------------------------------------------- building a network from Python's

std::shared_ptr<Network> make_network(const std::string& area, nb::list roads, const std::vector<std::string>& junctions,
                                      nb::list exits, const std::vector<int>& entries, const std::vector<int>& large_entries,
                                      nb::list grid) {
    auto network = std::make_shared<Network>();
    network->area = area;
    network->junctions = junctions;
    for (size_t i = 0; i < junctions.size(); ++i) network->junction_index.emplace(junctions[i], static_cast<int>(i));
    std::unordered_map<std::string, int> road_ids;
    for (nb::handle item : roads) {
        nb::tuple source = nb::cast<nb::tuple>(item);
        Road road;
        road.index = nb::cast<int>(source[0]);
        if (road.index != static_cast<int>(network->roads.size())) throw std::invalid_argument("roads out of order");
        std::string road_id = nb::cast<std::string>(source[1]);
        road.road_id = road_ids.emplace(road_id, static_cast<int>(road_ids.size())).first->second;
        road.lanes = nb::cast<int>(source[2]);
        road.speed = nb::cast<double>(source[3]);
        road.oneway = nb::cast<bool>(source[4]);
        road.service = nb::cast<bool>(source[5]);
        road.from_node = network->node(nb::cast<int64_t>(source[6]));
        road.to_node = network->node(nb::cast<int64_t>(source[7]));
        road.xs = nb::cast<std::vector<double>>(source[8]);
        road.ys = nb::cast<std::vector<double>>(source[9]);
        road.along = nb::cast<std::vector<double>>(source[10]);
        road.length = nb::cast<double>(source[11]);
        road.sx = nb::cast<double>(source[12]);
        road.sy = nb::cast<double>(source[13]);
        road.ex = nb::cast<double>(source[14]);
        road.ey = nb::cast<double>(source[15]);
        for (nb::handle stop : nb::cast<nb::list>(source[16])) {
            auto [at, offset, phase, junction] = nb::cast<std::tuple<double, int, int, int>>(stop);
            if (junction < 0 || static_cast<size_t>(junction) >= junctions.size()) throw std::invalid_argument("unknown junction");
            road.stops.push_back({at, offset, phase, junction});
        }
        road.rank = nb::cast<double>(source[17]);
        road.weight = nb::cast<double>(source[18]);
        if (road.xs.size() < 2 || road.xs.size() != road.ys.size() || road.xs.size() != road.along.size() || road.lanes < 1)
            throw std::invalid_argument("malformed road");
        network->roads.push_back(std::move(road));
    }
    for (nb::handle item : exits) {
        auto [node, leaving] = nb::cast<std::pair<int64_t, std::vector<int>>>(item);
        network->exits[network->node(node)] = leaving;
    }
    auto check = [&](int road) {
        if (road < 0 || static_cast<size_t>(road) >= network->roads.size()) throw std::invalid_argument("unknown road");
        return road;
    };
    for (const auto& leaving : network->exits)
        for (int road : leaving) check(road);
    for (int road : entries) network->entries.push_back(check(road));
    for (int road : large_entries) network->large_entries.push_back(check(road));
    for (nb::handle item : grid) {
        auto [x, y, near] = nb::cast<std::tuple<int64_t, int64_t, std::vector<int>>>(item);
        for (int road : near) check(road);
        network->grid[{x, y}] = near;
    }
    return network;
}

std::shared_ptr<Route> make_route(const std::vector<std::pair<double, double>>& points, const std::vector<double>& along, double length, bool closed,
                    const std::vector<std::pair<double, double>>& bends, const std::string& sig) {
    if (points.size() < 2 || points.size() != along.size()) throw std::invalid_argument("malformed route");
    auto route = std::make_shared<Route>();
    for (const auto& [x, y] : points) route->points.push_back({x, y});
    route->along = along;
    route->length = length;
    route->closed = closed;
    route->bends = bends;
    route->sig = sig;
    return route;
}

nb::dict vehicle_dict(const Traffic& traffic, const Vehicle& v) {
    nb::dict d;
    d["slot"] = v.slot;
    d["seed"] = v.seed;
    d["id"] = traffic.sample().ids[v.slot];
    d["model"] = traffic.sample().models[v.slot];
    d["length"] = v.length;
    d["half"] = v.half;
    d["half_width"] = v.half_width;
    d["accel"] = v.accel;
    d["large"] = v.large;
    d["speed_factor"] = v.speed_factor;
    d["rng"] = v.rng;
    d["road"] = v.road;
    d["lane"] = v.lane;
    d["lane_index"] = v.lane_index;
    d["distance"] = v.distance;
    d["speed"] = v.speed;
    d["target_speed"] = v.target_speed;
    d["stopped_s"] = v.stopped_s;
    d["wait_s"] = v.wait_s;
    d["aside"] = v.aside;
    d["target_aside"] = v.target_aside;
    d["outside"] = v.outside;
    d["ahead"] = v.ahead;
    nb::list claims;
    for (const Claim& c : v.claims)
        claims.append(nb::make_tuple(traffic.network().node_ids[c.node], c.from_r, c.from_lane, c.to_r, c.to_lane, c.entered));
    d["claims"] = claims;
    d["x"] = v.x;
    d["y"] = v.y;
    d["fx"] = v.fx;
    d["fy"] = v.fy;
    d["placed"] = v.placed;
    return d;
}

nb::dict shuttle_dict(const Shuttle& s) {
    nb::dict d;
    d["id"] = s.id;
    d["x"] = s.x;
    d["y"] = s.y;
    d["fx"] = s.fx;
    d["fy"] = s.fy;
    d["half_length"] = s.half_length;
    d["half_width"] = s.half_width;
    d["speed"] = s.speed;
    d["driven"] = s.driven;
    d["gives_way_to"] = s.gives_way_to;
    d["wait_s"] = s.wait_s;
    d["stops_within_m"] = s.stops_within_m;
    d["in_traffic"] = s.in_traffic;
    d["aside"] = s.aside;
    d["target_aside"] = s.target_aside;
    d["placed"] = s.placed;
    d["route_sig"] = s.route_sig;
    d["along"] = s.along;
    return d;
}

void set_shuttle_field(Shuttle& s, size_t vehicles, const std::string& name, nb::handle value) {
    if (name == "x") s.x = nb::cast<double>(value);
    else if (name == "y") s.y = nb::cast<double>(value);
    else if (name == "fx") s.fx = nb::cast<double>(value);
    else if (name == "fy") s.fy = nb::cast<double>(value);
    else if (name == "half_length") s.half_length = nb::cast<double>(value);
    else if (name == "half_width") s.half_width = nb::cast<double>(value);
    else if (name == "speed") s.speed = nb::cast<double>(value);
    else if (name == "driven") s.driven = nb::cast<bool>(value);
    else if (name == "wait_s") s.wait_s = nb::cast<double>(value);
    else if (name == "stops_within_m") s.stops_within_m = nb::cast<double>(value);
    else if (name == "in_traffic") s.in_traffic = nb::cast<bool>(value);
    else if (name == "aside") s.aside = nb::cast<double>(value);
    else if (name == "target_aside") s.target_aside = nb::cast<double>(value);
    else if (name == "placed") s.placed = nb::cast<bool>(value);
    else if (name == "route_sig") s.route_sig = nb::cast<std::string>(value);
    else if (name == "along") s.along = nb::cast<double>(value);
    else if (name == "gives_way_to") {
        std::vector<int> slots;
        for (nb::handle slot : value) {
            int index = nb::cast<int>(slot);
            if (index < 0 || static_cast<size_t>(index) >= vehicles) throw std::invalid_argument("no such vehicle");
            slots.push_back(index);
        }
        std::sort(slots.begin(), slots.end());
        slots.erase(std::unique(slots.begin(), slots.end()), slots.end());
        s.gives_way_to = std::move(slots);
    } else
        throw nb::attribute_error(name.c_str());
}

// The traffic's checkpoint, exactly as city_traffic.CityTraffic.state builds it
nb::dict traffic_state(Traffic& traffic) {
    const Network& net = traffic.network();
    nb::list vehicles;
    for (const Vehicle& v : traffic.vehicles()) {
        nb::list claims;
        for (const Claim& c : v.claims) {
            nb::list claim;
            claim.append(net.node_ids[c.node]);
            claim.append(c.from_r);
            claim.append(c.from_lane);
            claim.append(c.to_r);
            claim.append(c.to_lane);
            claim.append(static_cast<int>(c.entered));
            claims.append(claim);
        }
        nb::list ahead;
        for (int road : v.ahead) ahead.append(road);
        nb::list item;
        item.append(v.road);
        item.append(v.lane);
        item.append(v.distance);
        item.append(v.speed);
        item.append(v.stopped_s);
        item.append(v.wait_s);
        item.append(v.aside);
        item.append(static_cast<int>(v.outside));
        item.append(v.rng);
        item.append(ahead);
        item.append(claims);
        vehicles.append(item);
    }
    nb::list shuttles;
    for (const Shuttle& s : traffic.shuttles()) {
        nb::list gives;
        for (int slot : s.gives_way_to) gives.append(slot);
        nb::list item;
        item.append(s.id);
        item.append(s.x);
        item.append(s.y);
        item.append(s.fx);
        item.append(s.fy);
        item.append(s.half_length);
        item.append(s.half_width);
        item.append(s.speed);
        item.append(static_cast<int>(s.driven));
        item.append(gives);
        item.append(s.wait_s);
        item.append(s.stops_within_m);
        item.append(static_cast<int>(s.in_traffic));
        item.append(s.aside);
        item.append(s.target_aside);
        item.append(static_cast<int>(s.placed));
        item.append(s.along);
        item.append(s.route_sig);
        shuttles.append(item);
    }
    nb::list hazards;
    for (const auto& [road, spans] : traffic.hazards().items) {
        nb::list listed;
        for (const Span& span : spans) {
            nb::list pair;
            pair.append(span.start);
            pair.append(span.end);
            listed.append(pair);
        }
        nb::list item;
        item.append(road);
        item.append(listed);
        hazards.append(item);
    }
    nb::list pins;
    for (const Pin& pin : traffic.pins()) {
        nb::list slots;
        for (int slot : pin.slots) slots.append(slot);
        nb::list item;
        item.append(pin.key);
        item.append(slots);
        item.append(pin.until);
        pins.append(item);
    }
    nb::list faults;
    for (const Fault& fault : traffic.faults()) {
        nb::list item;
        item.append(fault.junction);
        item.append(fault.until);
        faults.append(item);
    }
    nb::dict state;
    state["area"] = net.area;
    state["time_s"] = traffic.time_s;
    state["red_light_runs"] = traffic.red_light_runs;
    state["vehicles"] = vehicles;
    state["shuttles"] = shuttles;
    state["hazards"] = hazards;
    state["pins"] = pins;
    state["signal_faults"] = faults;
    return state;
}

nb::dict constants() {
    nb::dict d;
#define F9_CONSTANT(name) d[#name] = name
    F9_CONSTANT(PLAN_AHEAD_M);
    F9_CONSTANT(MAX_PLANNED_ROADS);
    F9_CONSTANT(REACTION_S);
    F9_CONSTANT(COMFORT_BRAKE);
    F9_CONSTANT(HARD_BRAKE);
    F9_CONSTANT(YELLOW_BRAKE);
    F9_CONSTANT(MIN_GAP_M);
    F9_CONSTANT(STOP_MARGIN_M);
    F9_CONSTANT(NODE_STOP_BACK_M);
    F9_CONSTANT(PAST_LINE_M);
    F9_CONSTANT(CLAIM_MARGIN_M);
    F9_CONSTANT(NOTICE_S);
    F9_CONSTANT(CLEARED_JUNCTION_M);
    F9_CONSTANT(WAIT_PER_RANK_S);
    F9_CONSTANT(STANDING_MPS);
    F9_CONSTANT(STOPPED_MPS);
    F9_CONSTANT(RELEASE_CLAIM_S);
    F9_CONSTANT(STUCK_S);
    F9_CONSTANT(ENTER_MPS);
    F9_CONSTANT(SHUTTLE_SIGHT_M);
    F9_CONSTANT(SHUTTLE_CLEARANCE_M);
    F9_CONSTANT(SHUTTLE_BESIDE_M);
    F9_CONSTANT(SHUTTLE_MOVING_MPS);
    F9_CONSTANT(SHUTTLE_PASS_BEHIND_M);
    F9_CONSTANT(SHUTTLE_PASS_AHEAD_M);
    F9_CONSTANT(MAX_ASIDE_M);
    F9_CONSTANT(ASIDE_MPS);
    F9_CONSTANT(PASSING_MPS);
    F9_CONSTANT(SHUTTLE_JUNCTION_M);
    F9_CONSTANT(STANDING_SHUTTLE_JUNCTION_M);
    F9_CONSTANT(SHUTTLE_LANE_MPS);
    F9_CONSTANT(SHUTTLE_LOOK_M);
    F9_CONSTANT(SHUTTLE_LOOK_STEP_M);
    F9_CONSTANT(SHUTTLE_SIDE_ROOM_M);
    F9_CONSTANT(SHUTTLE_ONCOMING_ROOM_M);
    F9_CONSTANT(SHUTTLE_ROAD_ALIGN);
    F9_CONSTANT(SHUTTLE_FACING_SPAN_M);
    F9_CONSTANT(SHUTTLE_TAKE_TURN_S);
    F9_CONSTANT(SHUTTLE_GIVE_UP_S);
    F9_CONSTANT(SHUTTLE_CROSS_NOTICE_M);
    F9_CONSTANT(SHUTTLE_CROSS_NOTICE_S);
    F9_CONSTANT(SHUTTLE_JUMP_M);
    F9_CONSTANT(RUN_INTO_M);
    F9_CONSTANT(SHUTTLE_BODY_REACH_M);
    F9_CONSTANT(SHUTTLE_FOLLOW_EXTRA_M);
    F9_CONSTANT(SHUTTLE_SWING_M);
    F9_CONSTANT(TOUCH_M);
    F9_CONSTANT(NO_LIMIT);
    F9_CONSTANT(PIN_PICK_M);
    F9_CONSTANT(PIN_TOUCH_M);
    F9_CONSTANT(PINNED_PASS_MPS);
    F9_CONSTANT(LANE_WIDTH_M);
    F9_CONSTANT(MOVEMENT_REACH_M);
    F9_CONSTANT(CONFLICT_M);
    F9_CONSTANT(CELL_M);
    F9_CONSTANT(MATCH_M);
    F9_CONSTANT(GREEN_S);
    F9_CONSTANT(YELLOW_S);
    F9_CONSTANT(ALL_RED_S);
    F9_CONSTANT(PHASES);
#undef F9_CONSTANT
    return d;
}

// For tests: these helpers on many inputs, so the Python side can hold them to CPython's own
std::vector<double> math_check(const std::string& op, const std::vector<double>& a, const std::vector<double>& b) {
    std::vector<double> out(a.size());
    for (size_t i = 0; i < a.size(); ++i) {
        double x = a[i], y = i < b.size() ? b[i] : 0.0;
        if (op == "hypot") out[i] = py_hypot(x, y);
        else if (op == "square") out[i] = py_square(x);
        else if (op == "mod") out[i] = py_mod(x, y);
        else if (op == "atan2") out[i] = py_atan2(x, y);
        else if (op == "degrees") out[i] = RAD_TO_DEG * x;
        else if (op == "acos") out[i] = crt_acos(x);
        else if (op == "angle") out[i] = angle_deg(x, y, y, x);
        else throw std::invalid_argument(op);
    }
    return out;
}

}  // namespace

NB_MODULE(_traffic_native, m) {
    m.doc() = "The background traffic of src.traffic.city_traffic, natively (see src.traffic.native_traffic)";
    m.attr("SOURCE_HASH") = F9_TRAFFIC_SOURCE_HASH;
    // The road network a process caches (road_network's lru_cache) lives until the interpreter is gone,
    // which nanobind would otherwise report as a leak on every exit
    nb::set_leak_warnings(false);
    m.def("constants", &constants);
    m.def("math_check", &math_check, "op"_a, "a"_a, "b"_a);
    m.def("py_sum", &py_sum);

    nb::class_<Network>(m, "Network")
        .def_prop_ro("road_count", [](const Network& n) { return n.roads.size(); })
        .def("sample", [](const Network& n, int road, double distance, double left) {
            Pose p = n.roads.at(road).sample(distance, left);
            return std::make_tuple(p.x, p.y, p.fx, p.fy);
        })
        .def("project", [](const Network& n, int road, double x, double y) { return n.roads.at(road).project(x, y); })
        .def("road_under", &Network::road_under)
        .def("turn", [](const Network& n, int a, int b) { return n.turn(n.roads.at(a), n.roads.at(b)); })
        .def("conflict", [](const Network& n, std::tuple<int, int, int, int> a, std::tuple<int, int, int, int> b) {
            auto [ar, al, an, anl] = a;
            auto [br, bl, bn, bnl] = b;
            return n.conflict({ar, al, an, anl}, {br, bl, bn, bnl});
        });

    m.def("make_network", &make_network, "area"_a, "roads"_a, "junctions"_a, "exits"_a, "entries"_a, "large_entries"_a, "grid"_a);

    nb::class_<Route>(m, "Route")
        .def_ro("sig", &Route::sig)
        .def("point", [](const Route& r, double at) { Vec2 p = r.point(at); return std::make_pair(p.x, p.y); })
        .def("direction", [](const Route& r, double at) { Vec2 p = r.direction(at); return std::make_pair(p.x, p.y); })
        .def("body", [](const Route& r, double at, double aside, double half_length) {
            Pose p = r.body(at, aside, half_length);
            return std::make_tuple(p.x, p.y, p.fx, p.fy);
        });
    m.def("make_route", &make_route, "points"_a, "along"_a, "length"_a, "closed"_a, "bends"_a, "sig"_a);

    nb::class_<Traffic>(m, "Traffic")
        .def("__init__", [](Traffic* self, std::shared_ptr<Network> network) { new (self) Traffic(std::move(network)); })
        .def("add_vehicle", &Traffic::add_vehicle, "seed"_a, "id"_a, "model"_a, "length"_a, "half"_a, "half_width"_a, "large"_a,
             "accel"_a, "speed_factor"_a, "rng"_a, "road"_a, "lane"_a, "distance"_a, "speed"_a)
        .def("pose_all", &Traffic::pose_all)
        .def("copy", &Traffic::copy)
        .def_rw("time_s", &Traffic::time_s)
        .def_rw("red_light_runs", &Traffic::red_light_runs)
        .def("step",
             [](Traffic& t, double dt, double time) {
                 nb::gil_scoped_release unlocked;
                 t.step(dt, time);
             })
        .def("set_hazards", &Traffic::set_hazards, "found"_a, "replan"_a = true)
        .def("hazards",
             [](const Traffic& t) {
                 std::vector<std::pair<int, std::vector<std::pair<double, double>>>> out;
                 for (const auto& [road, spans] : t.hazards().items) {
                     std::vector<std::pair<double, double>> listed;
                     for (const Span& span : spans) listed.emplace_back(span.start, span.end);
                     out.emplace_back(road, std::move(listed));
                 }
                 return out;
             })
        .def("pin_vehicle", &Traffic::pin_vehicle)
        .def("pin_until", &Traffic::pin_until)
        .def("pins",
             [](const Traffic& t) {
                 std::vector<std::tuple<std::string, std::vector<int>, double>> out;
                 for (const Pin& pin : t.pins()) out.emplace_back(pin.key, pin.slots, pin.until);
                 return out;
             })
        .def("pinned",
             [](const Traffic& t) {
                 std::vector<int> out;
                 for (size_t i = 0; i < t.pinned().size(); ++i)
                     if (t.pinned()[i]) out.push_back(static_cast<int>(i));
                 return out;
             })
        .def("set_signal_fault", &Traffic::set_signal_fault)
        .def("signal_faults",
             [](const Traffic& t) {
                 std::vector<std::pair<std::string, double>> out;
                 for (const Fault& fault : t.faults()) out.emplace_back(fault.junction, fault.until);
                 return out;
             })
        .def("vehicle_count", [](const Traffic& t) { return t.vehicles().size(); })
        .def("vehicle", [](Traffic& t, int slot) { return vehicle_dict(t, t.vehicle_checked(slot)); })
        .def("vehicle_field",
             [](Traffic& t, int slot, const std::string& name) -> nb::object {
                 const Vehicle& v = t.vehicle_checked(slot);
                 if (name == "distance") return nb::cast(v.distance);
                 if (name == "half") return nb::cast(v.half);
                 if (name == "slot") return nb::cast(v.slot);
                 if (name == "speed") return nb::cast(v.speed);
                 if (name == "outside") return nb::cast(v.outside);
                 if (name == "placed") return nb::cast(v.placed);
                 if (name == "x") return nb::cast(v.x);
                 if (name == "y") return nb::cast(v.y);
                 if (name == "road") return nb::cast(v.road);
                 nb::dict fields = vehicle_dict(t, v);
                 if (!fields.contains(name.c_str())) throw nb::attribute_error(name.c_str());
                 nb::object value = fields[name.c_str()];
                 return value;
             })
        .def("shuttle", [](Traffic& t, const std::string& id, double half_length,
                           double half_width) { t.shuttle(id, half_length, half_width); })
        .def("shuttle_ids",
             [](Traffic& t) {
                 std::vector<std::string> out;
                 for (const Shuttle& s : t.shuttles()) out.push_back(s.id);
                 return out;
             })
        .def("shuttle_get", [](Traffic& t, const std::string& id) { return shuttle_dict(t.shuttle_named(id)); })
        .def("shuttle_set", [](Traffic& t, const std::string& id, const std::string& name,
                               nb::handle value) { set_shuttle_field(t.shuttle_named(id), t.vehicles().size(), name, value); })
        .def("shuttle_route_set", [](Traffic& t, const std::string& id, std::shared_ptr<Route> route) { t.shuttle_named(id).route = route; },
             "id"_a, "route"_a.none())
        .def("forget_shuttle", &Traffic::forget_shuttle)
        .def("plan_shuttle",
             [](Traffic& t, const std::string& id, std::shared_ptr<Route> route, double along, double cruise) {
                 Shuttle& s = t.shuttle_named(id);
                 Plan plan;
                 {
                     nb::gil_scoped_release unlocked;
                     plan = t.plan_shuttle(s, *route, along, cruise);
                 }
                 return std::make_tuple(plan.limit_mps, plan.cap_m, plan.held, plan.bend_mps);
             })
        .def(
            "place_shuttle",
            [](Traffic& t, const std::string& id, std::shared_ptr<Route> route, double along, double speed, double dt, bool held_by_traffic,
               bool held_by_light, bool in_traffic, double px, double py) {
                Shuttle& s = t.shuttle_named(id);
                RoutePtr shared = route;
                nb::gil_scoped_release unlocked;
                t.place_shuttle(s, shared, along, speed, dt, held_by_traffic, held_by_light, in_traffic, px, py);
            },
            "id"_a, "route"_a.none(), "along"_a, "speed"_a, "dt"_a, "held_by_traffic"_a, "held_by_light"_a, "in_traffic"_a, "px"_a,
            "py"_a)
        .def("body_blocker",
             [](Traffic& t, const std::string& id, std::shared_ptr<Route> route, double along) {
                 Shuttle& s = t.shuttle_named(id);
                 Blocker blocker;
                 {
                     nb::gil_scoped_release unlocked;
                     blocker = t.body_blocker(s, *route, along);
                 }
                 std::string peer = blocker.kind == 2 ? t.shuttles()[blocker.index].id : std::string();
                 return std::make_tuple(blocker.kind, blocker.index, peer);
             })
        .def("something_alongside",
             [](Traffic& t, const std::string& id, double shift) { return t.something_alongside(t.shuttle_named(id), shift); })
        .def("clear_shuttles",
             [](Traffic& t) {
                 nb::gil_scoped_release unlocked;
                 t.clear_shuttles();
             })
        .def("frame",
             [](const Traffic& t) {
                 std::pair<std::string, int> packed;
                 {
                     nb::gil_scoped_release unlocked;
                     packed = t.frame();
                 }
                 return std::make_pair(nb::bytes(packed.first.data(), packed.first.size()), packed.second);
             })
        .def("report", &Traffic::report)
        .def("shuttle_overlaps", &Traffic::shuttle_overlaps)
        .def("state", &traffic_state)
        .def("load", &Traffic::load)
        .def("load_events", &Traffic::load_events);
}
