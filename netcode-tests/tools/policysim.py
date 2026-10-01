"""Pace-policy simulator for ZeroLag.

Plant: N players, each with a capacity c_i(t) (fraction of normal game speed their PC sustains),
short-term capacity noise, optional connection holes, and measurement jitter. The server sets a
pace once per second (the real control interval); a client receives the new tick scale one
interval later. Lag L_i (game-seconds of backlog) grows by pace*dt - processed, where the client
processes min(capacity, requested catch-up rate, what the server has produced).

Controller: a replica of the v1.1 FrameScheduler pace logic (lag budget, hole-aware rate,
probe/hold with doubling, probe-failure detection, per-client tick scaling), with switches for
the proposed changes:
  measured_revert     failed probe -> revert to the measured rate, bounded by probe start/failure
  post_recovery_hold  a slowdown within 30 s of reaching full speed counts as a failed probe
  cubic_ramp          probe slowly near the last known ceiling, fast away from it
  dither              during a hold, modulate the pace +-A and re-probe when the lag stops responding
  rate_trigger        slow down early when the projected lag exceeds the budget
  creep               (v1.2) hold near the measured ceiling, creeping up, pulled back by the lateness level
  frames              (v1.3) the hold and probe-failure test read the backlog in net frames (frame lag minus the
                      client's in-flight minimum) and its growth, which a constant lateness offset cannot disturb

The plant can give a client a constant lateness offset (offset=, ms: an RTT estimate error or any other
constant in the server's slack measure) and gives each client 1-4 in-flight frames; the 'offset*' scenarios
reproduce the super maq hold of 1 Oct 2026, which v1.2 never released.

Usage: python3 policysim.py [--seeds N] [--minutes M] [--json out.json] [--trace scenario policy]
"""
import math, random, statistics, collections, json, sys, argparse

TIMESTEP = 40.0            # ms, normal speed
NOMINAL = 3 * TIMESTEP     # ms per net frame at normal speed
INTERVAL = 1.0             # s, control interval

# ----------------------------------------------------------------------------- plant

class Player:
    def __init__(self, name, cap_fn, jitter=10.0, hole_rate=0.0, hole_median=0.5, noise_sigma=0.04, rng=None, offset=0.0):
        self.name = name
        self.cap_fn = cap_fn
        self.jitter = jitter
        self.offset = offset     # ms the server's lateness measure reads high for this client (RTT estimate error etc.)
        self.inflight = rng.randint(1, 4) if rng else 2   # net frames between the server's frontier and a client that keeps up (its RTT)
        self.lag_frames = 0.0    # the server's per-interval reading of frames closed since the client's last reported frame
        self.hole_rate = hole_rate
        self.hole_median = hole_median
        self.noise_sigma = noise_sigma
        self.rng = rng
        self.L = 0.0             # backlog, game-seconds at normal speed (beyond the base buffer)
        self.eps = 0.0           # AR(1) capacity noise
        self.stall_left = 0.0    # seconds of an ongoing hole
        self.frames = 0.0        # game-seconds processed (the client's reported frame, in seconds)
        self.rate_req = 1.0      # requested rate (normal-speed units), from the last tick scale received
        self.pending_rate = 1.0  # rate the server just asked for; delivered next interval
        self.hole_time = 0.0     # seconds stalled in the last interval (the server detects holes)
        self.cap_now = 1.0

    def capacity(self, t):
        self.eps = 0.8 * self.eps + self.rng.gauss(0, self.noise_sigma) * math.sqrt(1 - 0.64)
        return max(0.05, self.cap_fn(t) * (1 + self.eps))

    def step(self, t, pace_speed, dt=INTERVAL):
        """pace_speed: the server's speed as a fraction of normal (1/pace)."""
        self.rate_req = self.pending_rate  # one-interval delivery delay
        c = self.cap_now = self.capacity(t)
        # holes
        hole = 0.0
        if self.stall_left > 0:
            hole = min(dt, self.stall_left); self.stall_left -= hole
        elif self.hole_rate > 0 and self.rng.random() < self.hole_rate * dt:
            h = self.hole_median * math.exp(self.rng.gauss(0, 0.6))
            hole = min(dt, h); self.stall_left = h - hole
        self.hole_time = hole
        active = dt - hole
        produced = pace_speed * dt
        processed = min(c * active, self.rate_req * active, self.L + produced)
        self.L += produced - processed
        self.frames += processed
        # measured lateness (ms of wall clock at the current pace) with jitter
        b = self.L / pace_speed * 1000.0 + self.offset + self.rng.gauss(0, self.jitter)
        # frame lag: backlog in net frames plus the in-flight frames, sampled at a random phase (+-1 frame)
        self.lag_frames = max(0, round(self.L / (NOMINAL / 1000.0) + self.inflight + self.rng.uniform(-1, 1)))
        return b

# ----------------------------------------------------------------------------- controller

class Policy:
    def __init__(self, name, measured_revert=False, post_recovery_hold=False, cubic_ramp=False,
                 dither=False, rate_trigger=False, dither_amp=0.05, dither_half=3, dither_thresh=0.25,
                 drain_headroom=None, trigger_horizon=10.0, lag_budget=3000, dither_max_hold=300.0,
                 dither_phases=4, headroom=0.97, hold_doubling=True, post_recovery_mode='double', evidence_ttl=45.0,
                 drain_gain=0.2, drain_cap=0.06, ramp_limit=1.0,
                 creep=False, creep_rate=0.2, creep_gain=5.0, creep_deadband=100.0, creep_free=15.0, creep_free_b=50.0,
                 trend=False, growth_deadband=50.0, growth_gain=0.1, level_soft=1000.0, level_gain=2.0, level_gate=1500.0,
                 relative=False, excess_creep=150.0, excess_pull=400.0, min_decay=20.0, stall_timeout=1e9,
                 frames=False, f_growth_deadband=0.5, f_deficit_deadband=0.05, f_growth_gain=1.0, f_backlog_creep=2.0, f_backlog_pull=5.0, f_level_gain=0.3,
                 f_fail=False, f_fail_growth=None, caution_band=0.0,
                 v10=False, v10_blind=False):
        self.name = name
        self.measured_revert = measured_revert
        self.post_recovery_hold = post_recovery_hold
        self.cubic_ramp = cubic_ramp
        self.dither = dither
        self.rate_trigger = rate_trigger
        self.dither_amp = dither_amp
        self.dither_half = dither_half
        self.dither_thresh = dither_thresh
        self.drain_headroom = drain_headroom
        self.trigger_horizon = trigger_horizon
        self.lag_budget = lag_budget
        self.dither_max_hold = dither_max_hold
        self.dither_phases = dither_phases
        self.headroom = headroom
        self.hold_doubling = hold_doubling
        self.post_recovery_mode = post_recovery_mode
        self.evidence_ttl = evidence_ttl
        self.drain_gain = drain_gain
        self.drain_cap = drain_cap
        self.ramp_limit = ramp_limit
        self.creep = creep
        self.creep_rate = creep_rate
        self.creep_gain = creep_gain
        self.creep_deadband = creep_deadband
        self.creep_free = creep_free
        self.creep_free_b = creep_free_b
        self.trend = trend                      # hold reacts to lag growth, not lag level
        self.growth_deadband = growth_deadband  # ms/s of lag growth tolerated as noise
        self.growth_gain = growth_gain          # speed points per (ms/s) of growth beyond the deadband
        self.level_soft = level_soft            # ms of lag above which the hold also pulls back gently
        self.level_gain = level_gain            # speed points per second per second of lag beyond level_soft
        self.level_gate = level_gate            # ms of lag beyond which the player does not count as keeping up
        self.relative = relative                # level terms relative to the lowest lateness seen in this hold
        self.excess_creep = excess_creep        # creep only while lateness is within this of the hold's low point (ms)
        self.excess_pull = excess_pull          # pull back when lateness exceeds the low point by more than this (ms)
        self.min_decay = min_decay              # the low point is allowed to rise this much per second (ms), so it tracks the recent floor
        self.stall_timeout = stall_timeout      # s: a hold this old counts free intervals even below the ceiling (a constant offset can stall the creep)
        self.frames = frames                    # hold driven by the backlog in net frames (frame lag minus the client's in-flight minimum) and its growth
        self.f_growth_deadband = f_growth_deadband  # frames/s of lag growth tolerated as noise (floor; the lag is sampled to the frame)
        self.f_deficit_deadband = f_deficit_deadband  # ...or this share of the frames closed per second at the pace, if larger
        self.f_growth_gain = f_growth_gain      # pull-back: this x (growth beyond the deadband, in frames/s) x NOMINAL/10 speed points per second
        self.f_backlog_creep = f_backlog_creep  # creep only while the backlog is at most this many frames
        self.f_backlog_pull = f_backlog_pull    # pull back while the backlog exceeds this many frames
        self.f_level_gain = f_level_gain        # speed points per second per frame of backlog beyond that
        self.f_fail = f_fail                    # probe failure judged on the frame backlog (>= 3 frames and growing), not on lateness ms
        self.f_fail_growth = f_fail_growth      # if set: 'growing' means the lag rose by at least this many frames over the last 3 intervals
        self.caution_band = caution_band        # points above the last measured ceiling within which a probe keeps its initial rate (no doubling)
        self.v10 = v10
        self.v10_blind = v10_blind

class CState:
    def __init__(self):
        self.last_behind = 0.0
        self.falling = 0
        self.behind_intervals = 0
        self.told_faster = False
        self.progress = collections.deque(maxlen=4)   # (t, frames)
        self.holes = collections.deque(maxlen=4)      # hole seconds per interval, aligned with progress
        self.b_hist = collections.deque(maxlen=4)
        self.lag_hist = collections.deque(maxlen=4)   # frame-lag readings
        self.min_lag = None                           # lowest frame lag seen: the client's in-flight frames

class Controller:
    PACE_HEADROOM = 0.97
    GAIN = 0.6
    MIN_TICK_SCALE = 0.7
    MAX_TICK_SCALE = 1.6
    MAX_CATCHUP = 4.0
    PROBE_RATE, MAX_PROBE_RATE, DOUBLING = 0.5, 4.0, 10.0
    MIN_HOLD, MAX_HOLD = 30.0, 120.0
    FAIL_LATENESS = 3
    FALLING_N, BEHIND_N = 3, 6
    SLACK_DEADBAND = 40.0
    MAX_PACE = 1 / 0.30

    def __init__(self, policy, names):
        self.p = policy
        self.PACE_HEADROOM = policy.headroom
        if policy.v10_blind:
            self.MAX_PACE = 10.0
        self.names = names
        self.s = {n: CState() for n in names}
        self.pace = 1.0            # multiplier >= 1 (speed = 1/pace)
        self.probe_rate = 0.0
        self.probe_since = 0.0
        self.probe_start_pace = 1.0
        self.hold_ms = 0.0         # seconds here
        self.hold_until = 0.0
        self.hold_for = None
        self.slowest = None
        self.full_since = -1e9
        self.last_ceiling = None   # speed % where a player last hit their ceiling
        self.cubic_cross = None    # time the probe crossed the last ceiling
        self.dither_sign = 0
        self.phase_start_b = None      # b of the held player at the start of the current + phase
        self.phase_deltas = collections.deque(maxlen=self.p.dither_phases)   # lag change over each completed + phase
        self.hold_speed = None         # the speed the hold reverted to (0.97 x measured capacity)
        self.hold_started = 0.0
        self.monitoring = False
        self.monitor_started = 0.0
        self.at_ceiling_until = -1e9
        self.hold_mean = None
        self.phase_mean = None
        self.free_since = None
        self.phase_evidence = collections.deque(maxlen=self.p.dither_phases)
        self.hold_min_b = None     # lowest lateness seen during this hold (tracks the client's constant offset)
        self.events = []

    # --- helpers
    def speed(self): return 100.0 / self.pace

    def catchup_tick(self, speed):
        return max(1.0, round(TIMESTEP / min(self.MAX_CATCHUP, max(1 / self.MIN_TICK_SCALE, speed))))

    def start_hold(self, now, who, double):
        if double:
            self.hold_ms = min(self.MAX_HOLD, max(self.MIN_HOLD, self.hold_ms * 2)) if self.p.hold_doubling else self.MIN_HOLD
        self.hold_until = now + self.hold_ms
        self.hold_for = who
        self.probe_rate = 0.0
        self.hold_started = now
        self.hold_speed = self.speed()
        self.phase_deltas.clear()
        self.phase_start_b = None
        self.monitoring = False
        self.at_ceiling_until = -1e9
        self.hold_mean = None
        self.phase_mean = None
        self.free_since = None
        self.phase_evidence.clear()
        self.hold_min_b = None

    def update(self, now, behind, frames, holes, applied_speed_prev, lagframes=None):
        lagframes = lagframes or {}
        """behind: name -> measured lateness ms; frames: name -> processed game-seconds; holes: name -> hole seconds this interval.
        Returns (mean_pace_speed, applied_speed, rates: name -> requested rate)."""
        P = self.p
        old_pace = self.pace
        for n in self.names:
            st = self.s[n]
            b = behind[n]
            if P.f_fail:
                lf0 = lagframes.get(n, 0)
                st.falling = st.falling + 1 if (st.told_faster and len(st.lag_hist) > 0 and lf0 > st.lag_hist[-1]) else 0
            else:
                st.falling = st.falling + 1 if (st.told_faster and b > st.last_behind) else 0
            st.behind_intervals = st.behind_intervals + 1 if (st.told_faster and b > 2 * NOMINAL) else 0
            st.progress.append((now, frames[n]))
            st.holes.append(holes[n])
            st.b_hist.append(b)
            lf = lagframes.get(n, 0)
            st.lag_hist.append(lf)
            st.min_lag = lf if st.min_lag is None else min(st.min_lag, lf)

        cannot_keep_up = False

        # dithered hold detector: at the end of each + phase, compare the held player's lag change with what a
        # ceiling at the estimate would have produced. Several phases without a response = the ceiling moved up.
        if P.dither and self.hold_for is not None and self.probe_rate == 0 and self.pace > 1 and self.hold_speed is not None:
            st = self.s[self.hold_for]
            if self.monitoring and self.dither_sign == 1 and self.phase_start_b is None:
                self.phase_start_b = st.b_hist[-2] if len(st.b_hist) >= 2 else behind[self.hold_for]
                self.phase_mean = self.hold_mean
            if self.monitoring and self.dither_sign == -1 and self.phase_start_b is not None:
                c_est = self.hold_speed / self.PACE_HEADROOM
                p_plus = self.phase_mean * (1 + P.dither_amp)
                expected = P.dither_half * max(0.0, p_plus - c_est) / p_plus * 1000.0
                nominal = P.dither_half * P.dither_amp / (1 + P.dither_amp) * 1000.0
                if expected >= 0.6 * nominal:
                    self.phase_evidence.append((st.b_hist[-2] - self.phase_start_b, expected))
                    if len(self.phase_evidence) >= P.dither_phases:
                        r = sum(d for d, e in self.phase_evidence) / sum(e for d, e in self.phase_evidence)
                        if r < P.dither_thresh:
                            self.events.append((now, 'reprobe', self.hold_mean))
                            self.hold_until = now
                            self.phase_evidence.clear()
                            self.at_ceiling_until = -1e9
                        else:
                            self.at_ceiling_until = now + P.evidence_ttl
                self.phase_start_b = None

        # probe failure
        if self.probe_rate > 0 and not P.v10:
            if P.f_fail and P.f_fail_growth is not None:
                failing = [n for n in self.names if len(self.s[n].lag_hist) >= 4 and self.s[n].told_faster
                           and self.s[n].lag_hist[-1] - (self.s[n].min_lag or 0) >= self.FAIL_LATENESS
                           and self.s[n].lag_hist[-1] - self.s[n].lag_hist[0] >= P.f_fail_growth]
            elif P.f_fail:
                failing = [n for n in self.names if self.s[n].lag_hist and self.s[n].lag_hist[-1] - (self.s[n].min_lag or 0) >= self.FAIL_LATENESS and self.s[n].falling >= self.FALLING_N]
            else:
                failing = [n for n in self.names if behind[n] > self.FAIL_LATENESS * NOMINAL and self.s[n].falling >= self.FALLING_N]
            if failing:
                who = failing[0]
                from_speed = self.speed()
                revert = self.probe_start_pace
                if P.measured_revert:
                    needed = self.needed_pace(who, now)
                    if needed is not None and needed != 'holes':
                        revert = min(self.probe_start_pace, max(self.pace, needed))
                        self.last_ceiling = 100.0 / needed * self.PACE_HEADROOM  # the measured capacity
                else:
                    self.last_ceiling = from_speed
                self.pace = max(self.pace, revert)
                self.start_hold(now, who, double=True)
                for n in failing:
                    self.s[n].falling = self.s[n].behind_intervals = 0
                self.events.append((now, 'fail', from_speed, self.speed(), who))
                cannot_keep_up = True

        # slowdowns
        for n in self.names:
            st = self.s[n]
            b = behind[n]
            trigger = b > P.lag_budget
            if P.rate_trigger and not trigger and b > 500 and len(st.b_hist) >= 4 and st.falling >= self.FALLING_N:
                slope = (st.b_hist[-1] - st.b_hist[0]) / 3.0
                trigger = b + slope * P.trigger_horizon > P.lag_budget
            if not trigger:
                continue
            if st.falling < self.FALLING_N and st.behind_intervals < self.BEHIND_N:
                continue
            needed = self.needed_pace(n, now)
            if needed is None:
                continue
            if needed == 'holes':
                st.falling = st.behind_intervals = 0
                continue
            if needed > self.MAX_PACE and len(self.names) >= 3:
                st.falling = st.behind_intervals = 0
                continue  # floor: left behind (not modelled further)
            if needed > self.pace + 0.005:
                double = False
                if self.hold_for != n:
                    self.hold_for = n; self.hold_ms = 0.0
                elif self.probe_rate > 0:
                    double = True
                elif P.post_recovery_hold and now - self.full_since < 30.0:
                    if P.post_recovery_mode == 'double':
                        double = True
                    else:
                        self.hold_ms = self.MIN_HOLD
                self.events.append((now, 'slow', self.speed(), 100.0 / needed, n))
                self.last_ceiling = 100.0 / needed * self.PACE_HEADROOM
                self.pace = max(self.pace, needed)
                self.start_hold(now, n, double=double)
            if needed > 1.005 and needed >= self.pace - 0.005:
                self.slowest = n
            st.falling = st.behind_intervals = 0
            cannot_keep_up = True

        # probe back up
        recovery = max(NOMINAL, P.lag_budget / 2)
        if P.v10:
            if not cannot_keep_up and self.pace > 1:
                if all(behind[n] <= NOMINAL for n in self.names):
                    self.pace = max(1.0, self.pace - 0.03)
                elif all(behind[n] <= recovery for n in self.names):
                    self.pace = max(1.0, self.pace - 0.01)
                if self.pace == 1.0 and old_pace > 1.0:
                    self.slowest = None
                    self.events.append((now, 'full', 100.0))
        hold_over = now >= self.hold_until
        if P.dither and hold_over and now < self.at_ceiling_until and now - self.hold_started < P.dither_max_hold:
            hold_over = False   # the dither recently confirmed the ceiling is still there; keep holding (up to a limit)
        if P.creep and self.hold_for is not None and self.probe_rate == 0 and self.pace > 1:
            # the creeping hold never times out on its own: it probes only once the held player has shown
            # no resistance (no lag) for creep_free seconds while the mean crept up
            b = behind.get(self.hold_for, 0.0)
            # as in the C#: count intervals with no resistance while 3% above the measured ceiling; resistance
            # (more than twice the deadband behind) resets the count; in between, and around holes, nothing changes
            at_ceiling = self.hold_mean is not None and self.hold_speed is not None and self.hold_mean >= 1.03 * self.hold_speed / self.PACE_HEADROOM
            st = self.s[self.hold_for]
            recent_hole = any(h > 0 for h in st.holes)
            if self.free_since is None: self.free_since = 0
            g = (st.b_hist[-1] - st.b_hist[0]) / max(1, len(st.b_hist) - 1) if len(st.b_hist) >= 2 else 0.0
            if P.frames:
                lh = st.lag_hist
                fg = (lh[-1] - lh[0]) / max(1, len(lh) - 1) if len(lh) >= 2 else 0.0
                backlog = lh[-1] - (st.min_lag or 0)
            if recent_hole:
                pass
            elif P.frames:
                fps = 1000.0 / (NOMINAL * self.pace)
                dead = max(P.f_deficit_deadband * fps, P.f_growth_deadband)
                if fg > 2 * dead or backlog > P.f_backlog_pull:
                    self.free_since = 0
                elif fg <= dead and backlog <= P.f_backlog_creep and at_ceiling:
                    self.free_since += 1
            elif P.trend:
                excess = (b - self.hold_min_b) if (P.relative and self.hold_min_b is not None) else 0.0
                if g > 2 * P.growth_deadband or (P.relative and excess > P.excess_pull):
                    self.free_since = 0
                elif g <= P.growth_deadband and b <= P.level_gate and excess <= P.excess_creep and (at_ceiling or now - self.hold_started >= P.stall_timeout):
                    self.free_since += 1
            elif b > 2 * P.creep_deadband:
                self.free_since = 0
            elif b < P.creep_deadband and at_ceiling:
                self.free_since += 1
            hold_over = self.free_since >= P.creep_free
            if hold_over: self.events.append((now, 'reprobe', self.hold_mean))
        if not P.v10 and not cannot_keep_up and self.pace > 1 and hold_over and all(behind[n] <= recovery for n in self.names):
            if self.probe_rate <= 0:
                if (P.dither or P.creep) and self.hold_mean is not None:
                    self.pace = 100.0 / self.hold_mean   # continue from where the hold actually was
                    self.hold_mean = None
                self.probe_rate = self.PROBE_RATE
                self.probe_since = now
                self.probe_start_pace = self.pace
                self.cubic_cross = None
                self.dither_sign = 0
            else:
                self.probe_rate = min(self.MAX_PROBE_RATE, self.PROBE_RATE * 2 ** ((now - self.probe_since) / self.DOUBLING))
            rate = self.probe_rate
            if P.caution_band > 0 and self.last_ceiling is not None and self.speed() < self.last_ceiling + P.caution_band:
                rate = self.PROBE_RATE
                self.probe_since = now   # doubling starts once past the band
            if P.cubic_ramp and self.last_ceiling is not None:
                s = self.speed()
                if s < self.last_ceiling - 2:
                    rate = min(4.0, max(0.5, 0.15 * (self.last_ceiling - s)))
                    self.probe_since = now  # doubling restarts once past the ceiling
                else:
                    if self.cubic_cross is None:
                        self.cubic_cross = now; self.probe_since = now
                    rate = min(self.MAX_PROBE_RATE, self.PROBE_RATE * 2 ** ((now - self.probe_since) / self.DOUBLING))
                self.probe_rate = max(rate, 1e-6)
            self.pace = max(1.0, 1 / (1 / self.pace + rate / 100.0))
            if self.pace == 1.0 and old_pace > 1.0:
                self.slowest = None
                self.probe_rate = 0.0
                self.hold_ms = 0.0
                self.full_since = now
                self.events.append((now, 'full', 100.0))

        # applied pace during a dithered hold: the mean sits at the ceiling estimate, pulled down in proportion to the
        # held player's backlog (so a backlog drains and noise is absorbed), moving at most ramp_limit points per
        # second; the dither rides on top while the backlog is small.
        mean_speed = self.speed()
        applied_speed = mean_speed
        if P.creep and self.pace > 1 and self.probe_rate == 0 and self.hold_for is not None and self.hold_speed is not None:
            b = max(0.0, behind.get(self.hold_for, 0.0))
            if self.hold_mean is None:
                self.hold_mean = self.hold_speed
            if P.frames:
                st = self.s[self.hold_for]
                lh = st.lag_hist
                fg = (lh[-1] - lh[0]) / max(1, len(lh) - 1) if len(lh) >= 2 else 0.0
                backlog = lh[-1] - (st.min_lag or 0)
                fps = 1000.0 / (NOMINAL * self.pace)
                dead = max(P.f_deficit_deadband * fps, P.f_growth_deadband)
                delta = (P.creep_rate if (fg <= dead and backlog <= P.f_backlog_creep) else 0.0) \
                    - P.f_growth_gain * max(0.0, fg - dead) * NOMINAL / 10.0 - P.f_level_gain * max(0.0, backlog - P.f_backlog_pull)
            elif P.trend and P.relative:
                st = self.s[self.hold_for]
                g = (st.b_hist[-1] - st.b_hist[0]) / max(1, len(st.b_hist) - 1) if len(st.b_hist) >= 2 else 0.0
                self.hold_min_b = b if self.hold_min_b is None else min(b, self.hold_min_b + P.min_decay)
                excess = b - self.hold_min_b
                delta = (P.creep_rate if (g <= P.growth_deadband and excess <= P.excess_creep) else 0.0) \
                    - P.growth_gain * max(0.0, g - P.growth_deadband) - P.level_gain * max(0.0, excess - P.excess_pull) / 1000.0
            elif P.trend:
                st = self.s[self.hold_for]
                g = (st.b_hist[-1] - st.b_hist[0]) / max(1, len(st.b_hist) - 1) if len(st.b_hist) >= 2 else 0.0
                delta = P.creep_rate - P.growth_gain * max(0.0, g - P.growth_deadband) - P.level_gain * max(0.0, b - P.level_soft) / 1000.0
            else:
                delta = P.creep_rate - P.creep_gain * max(0.0, b - P.creep_deadband) / 1000.0
            self.hold_mean += max(-P.ramp_limit, min(P.ramp_limit, delta))
            floor = self.hold_speed / self.PACE_HEADROOM * (1 - P.drain_cap)   # never far below the last measured ceiling
            self.hold_mean = min(100.0, max(self.hold_mean, floor, 30.0))
            mean_speed = applied_speed = self.hold_mean
            self.dither_sign = 0
        elif P.dither and self.pace > 1 and self.probe_rate == 0 and self.hold_for is not None and self.hold_speed is not None:
            b = max(0.0, behind.get(self.hold_for, 0.0))
            c_est = self.hold_speed / self.PACE_HEADROOM
            target = c_est * (1 - min(P.drain_cap, P.drain_gain * b / 1000.0))
            if self.hold_mean is None:
                self.hold_mean = self.hold_speed
            self.hold_mean += max(-P.ramp_limit, min(P.ramp_limit, target - self.hold_mean))
            mean_speed = self.hold_mean
            if b < 300:
                if not self.monitoring:
                    self.monitoring = True
                    self.monitor_started = now
                phase = int((now - self.monitor_started) // P.dither_half) % 2
                self.dither_sign = 1 if phase == 0 else -1
                applied_speed = mean_speed * (1 + P.dither_amp * self.dither_sign)
            else:
                self.monitoring = False
                self.phase_start_b = None
                self.dither_sign = 0
                applied_speed = mean_speed
        else:
            self.dither_sign = 0
            self.monitoring = False

        # tick scales
        rates = {}
        base = TIMESTEP / (applied_speed / 100.0)
        for n in self.names:
            st = self.s[n]
            b = behind[n]
            tick = round(base)
            fastest = TIMESTEP * self.MIN_TICK_SCALE
            if abs(b) >= self.SLACK_DEADBAND:
                tick = round(base - self.GAIN * b / (1000.0 / TIMESTEP))
            if b > 0 and not P.v10_blind:
                fastest = self.catchup_tick(1 + self.GAIN * b / 1000.0)
            st.last_behind = b
            tick = min(max(tick, fastest), max(self.MAX_TICK_SCALE * TIMESTEP, round(base * 1.25)))
            st.told_faster = tick < round(base)
            rates[n] = TIMESTEP / tick
        return mean_speed, applied_speed, rates

    def needed_pace(self, n, now):
        """Pace this player needs from its rate over the progress window, hole time removed. None if no data,
        'holes' if it keeps up between holes. (v1.0 did not know about holes.)"""
        st = self.s[n]
        if len(st.progress) < 2:
            return None
        (t0, f0), (t1, f1) = st.progress[0], st.progress[-1]
        elapsed = max(1e-6, t1 - t0)
        hole = 0.0 if self.p.v10_blind else sum(list(st.holes)[1:])
        rate = (f1 - f0) / max(0.2, elapsed - hole)   # normal-speed units
        if rate <= 0:
            return None
        if hole > 0 and rate * self.PACE_HEADROOM >= 1 / self.pace:
            return 'holes'
        return 1 / (self.PACE_HEADROOM * rate)

# ----------------------------------------------------------------------------- scenarios

def piecewise(points):
    def f(t):
        for (t0, v0), (t1, v1) in zip(points[:-1], points[1:]):
            if t <= t1:
                return v0 + (v1 - v0) * (t - t0) / max(1e-9, t1 - t0) if t >= t0 else v0
        return points[-1][1]
    return f

def ou_trace(rng, n, mean, sigma, tau, lo, hi):
    x, out = mean, []
    for _ in range(n):
        x += (mean - x) / tau + sigma * math.sqrt(2 / tau) * rng.gauss(0, 1)
        out.append(min(hi, max(lo, x)))
    return lambda t: out[min(n - 1, int(t))]

def spiky_trace(rng, n, base=0.92):
    out = [base] * n
    t = rng.expovariate(1 / 120.0)
    while t < n:
        depth = rng.uniform(0.40, 0.65); dur = rng.uniform(20, 60)
        for k in range(int(t), min(n, int(t + dur))):
            ramp = min(1.0, (k - t) / 10.0, (t + dur - k) / 10.0)
            out[k] = base - (base - depth) * max(0.0, ramp)
        t += dur + rng.expovariate(1 / 120.0)
    return lambda t: out[min(n - 1, int(t))]

def make_players(scenario, rng, seconds):
    fast = lambda t: 1.5
    others = [Player(f"fast{i}", fast, jitter=(30 if i == 0 else 10), rng=rng) for i in range(5)]
    if scenario == 'dip':
        slow = [Player('melo', piecewise([(0, 1.02), (300, 1.02), (360, 0.60), (480, 0.60), (540, 0.85), (900, 0.85), (960, 1.05), (seconds, 1.05)]), jitter=15, rng=rng)]
    elif scenario == 'wander':
        slow = [Player('k$', ou_trace(rng, seconds + 2, 0.78, 0.08, 90.0, 0.5, 1.2), jitter=15, rng=rng)]
    elif scenario == 'spiky':
        slow = [Player('aza', spiky_trace(rng, seconds + 2), jitter=15, rng=rng)]
    elif scenario == 'steady':
        slow = [Player('steady', lambda t: 0.75, jitter=15, rng=rng)]
    elif scenario == 'healthy':
        slow = [Player('edge', lambda t: 1.03, jitter=15, rng=rng)]
    elif scenario == 'lossy':
        slow = [Player('dazzle', lambda t: 1.2, jitter=20, hole_rate=0.25, hole_median=0.4, rng=rng)]
    elif scenario == 'offset':
        # a steady 75% player whose lateness reads 600 ms high, among players reading 0-300 ms high (super maq, Oct 1)
        slow = [Player('maq', lambda t: 0.75, jitter=15, rng=rng, offset=600.0)]
        for o in others: o.offset = rng.uniform(0, 300)
    elif scenario == 'edge':
        slow = [Player('sas', ou_trace(rng, seconds + 2, 0.90, 0.04, 90.0, 0.7, 1.1), jitter=15, rng=rng)]
    elif scenario == 'offsethi':
        slow = [Player('hi', piecewise([(0, 1.02), (300, 1.02), (360, 0.60), (480, 0.60), (540, 0.85), (900, 0.85), (960, 1.05), (seconds, 1.05)]), jitter=15, rng=rng, offset=950.0)]
        for o in others: o.offset = rng.uniform(0, 300)
    elif scenario == 'offsetdip':
        slow = [Player('melo', piecewise([(0, 1.02), (300, 1.02), (360, 0.60), (480, 0.60), (540, 0.85), (900, 0.85), (960, 1.05), (seconds, 1.05)]), jitter=15, rng=rng, offset=600.0)]
        for o in others: o.offset = rng.uniform(0, 300)
    elif scenario == 'twoslow':
        slow = [Player('slowA', ou_trace(rng, seconds + 2, 0.80, 0.06, 90.0, 0.5, 1.2), jitter=15, rng=rng),
                Player('slowB', ou_trace(rng, seconds + 2, 0.70, 0.06, 90.0, 0.5, 1.2), jitter=15, rng=rng)]
        others = others[:4]
    else:
        raise ValueError(scenario)
    return slow + others

SCENARIOS = ['dip', 'wander', 'spiky', 'steady', 'healthy', 'lossy', 'twoslow', 'offset', 'offsetdip', 'offsethi', 'edge']

POLICIES = [
    Policy('v1.0', v10=True, v10_blind=True),
    Policy('v1.0+holes', v10=True),
    Policy('v1.1'),
    Policy('+revert', measured_revert=True),
    Policy('dither', measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', dither=True, rate_trigger=True),
    Policy('creep0.2', measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.2),
    Policy('creep0.3', measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.3),
    Policy('creep0.4', measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.4),
    Policy('creepFast', measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.6, creep_deadband=300.0, creep_free=10.0),
    Policy('v1.2', measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.3),
    Policy('v1.3', measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.3,
           creep_free=15.0, frames=True, f_backlog_creep=2.0, f_backlog_pull=4.0, f_fail=True, f_fail_growth=2),
]

# ----------------------------------------------------------------------------- run

JITTER_SCALE = 1.0

def run(scenario, policy, seed, minutes=20, trace=False):
    rng = random.Random(seed)
    seconds = minutes * 60
    players = make_players(scenario, rng, seconds)
    for p in players:
        p.jitter *= JITTER_SCALE
    names = [p.name for p in players]
    ctl = Controller(policy, names)
    applied = 100.0; mean_speed = 100.0
    speeds, applied_speeds = [], []
    slow_b = []
    rows = []
    for t in range(seconds):
        behind, frames, holes, lagf = {}, {}, {}, {}
        for p in players:
            b = p.step(t, applied / 100.0)
            behind[p.name] = b; frames[p.name] = p.frames; holes[p.name] = p.hole_time; lagf[p.name] = p.lag_frames
        mean_speed, applied, rates = ctl.update(t + 1, behind, frames, holes, applied, lagf)
        for p in players:
            p.pending_rate = rates[p.name]
        speeds.append(mean_speed); applied_speeds.append(applied)
        slow_b.append(max(0.0, players[0].L / (applied / 100.0) * 1000.0))
        if trace:
            rows.append((t, round(players[0].cap_now * 100), round(mean_speed), round(applied), round(slow_b[-1])))
    hours = seconds / 3600.0
    ev = ctl.events
    kinds = collections.Counter(e[1] for e in ev)
    tv = sum(abs(a - b) for a, b in zip(speeds[1:], speeds[:-1]))
    tv_applied = sum(abs(a - b) for a, b in zip(applied_speeds[1:], applied_speeds[:-1]))
    changes = sum(1 for a, b in zip(speeds[1:], speeds[:-1]) if abs(a - b) >= 3)
    # how long the game stayed slowed after the slow player could keep up again (capacity >= 1 and lag gone)
    stuck = sum(1 for t in range(seconds) if applied_speeds[t] < 99.5 and players[0].cap_fn(t) >= 1.0 and slow_b[t] < 200)
    m = dict(
        stuck=stuck,
        lost=100 - statistics.mean(applied_speeds),
        below=100.0 * sum(1 for s in applied_speeds if s < 99.5) / len(applied_speeds),
        events_h=(kinds['slow'] + kinds['fail'] + kinds['full']) / hours,
        changes_h=changes / hours,
        tv_h=tv / hours, tv_applied_h=tv_applied / hours,
        slow_mean_b=statistics.mean(slow_b), slow_max_b=max(slow_b),
        slow_over1s=100.0 * sum(1 for b in slow_b if b > 1000) / len(slow_b),
        fails=kinds['fail'], slows=kinds['slow'], reprobes=kinds['reprobe'],
    )
    return m, ev, rows

SLOW_SCENARIOS = ['dip', 'wander', 'spiky', 'steady', 'twoslow', 'offset', 'offsetdip', 'offsethi', 'edge']

def sweep(args):
    base = dict(measured_revert=True, post_recovery_hold=True, dither=True, rate_trigger=True)
    T = dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.3, trend=True)
    if args.trend_sweep:
        variants = [
            ('v1.2', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.3)),
            ('soft800 g1.5 free30', dict(T, level_soft=800.0, level_gain=1.5, creep_free=30.0)),
            ('frames', dict(T, trend=False, frames=True)),
            ('frames free30', dict(T, trend=False, frames=True, creep_free=30.0)),
            ('frames free30 ffail', dict(T, trend=False, frames=True, creep_free=30.0, f_fail=True)),
            ('frames ffail g2 b4 (v1.3)', dict(T, trend=False, frames=True, f_fail=True, f_fail_growth=2, f_backlog_pull=4.0)),
            ('v1.3 free30', dict(T, trend=False, frames=True, creep_free=30.0, f_fail=True, f_fail_growth=2, f_backlog_pull=4.0)),
            ('v1.3 cautious probe', dict(T, trend=False, frames=True, f_fail=True, f_fail_growth=2, f_backlog_pull=4.0, caution_band=10.0)),
            ('frames creep1 pull4 ffail', dict(T, trend=False, frames=True, creep_free=30.0, f_backlog_creep=1.0, f_backlog_pull=4.0, f_fail=True)),
        ]
        print_sweep(args, variants); return
    variants = [
        ('v1.1', {}), ('+revert', dict(measured_revert=True)),
        ('+recovery(double)', dict(measured_revert=True, post_recovery_hold=True)),
        ('+recovery(30s)', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed')),
        ('revert,no hold doubling', dict(measured_revert=True, hold_doubling=False)),
        ('full A=2.5%', dict(base, dither_amp=0.025)), ('full A=5%', dict(base)), ('full A=7.5%', dict(base, dither_amp=0.075)),
        ('full A=5% 6 phases', dict(base, dither_phases=6)), ('full A=5% half=2s', dict(base, dither_half=2)), ('full A=5% half=5s', dict(base, dither_half=5)),
        ('full drain 6%', dict(base)), ('full drain 10%', dict(base, drain_headroom=0.10)),
        ('full horizon 5s', dict(base, trigger_horizon=5.0)), ('full horizon 20s', dict(base, trigger_horizon=20.0)),
        ('full no trigger', dict(measured_revert=True, post_recovery_hold=True, dither=True)),
        ('full headroom 0.95', dict(base, headroom=0.95)), ('full headroom 0.93', dict(base, headroom=0.93)),
        ('full recovery(30s)', dict(base, post_recovery_mode='fixed')),
        ('full, budget 2s', dict(base, lag_budget=2000)),
        ('creep 0.2/s', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True)),
        ('creep 0.1/s', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.1)),
        ('creep 0.4/s', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_rate=0.4)),
        ('creep gain 10', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_gain=10.0)),
        ('creep free 30s', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True, rate_trigger=True, creep_free=30.0)),
        ('creep no trigger', dict(measured_revert=True, post_recovery_hold=True, post_recovery_mode='fixed', creep=True)),
    ]
    print_sweep(args, variants)

def print_sweep(args, variants):
    print(f"{'variant':26} {'lost%':>6} {'events/h':>9} {'TV/h':>7} {'slow lag ms':>12} {'slow>1s%':>9} {'slow max ms':>12} {'stuck s':>8}   (mean over {', '.join(SLOW_SCENARIOS)}; {args.seeds} seeds)")
    out = {}
    for name, kw in variants:
        pol = Policy(name, **kw)
        per = {}
        for sc in SLOW_SCENARIOS:
            ms = [run(sc, pol, seed, args.minutes)[0] for seed in range(args.seeds)]
            per[sc] = {k: statistics.mean(m[k] for m in ms) for k in ms[0]}
        agg = {k: statistics.mean(per[sc][k] for sc in SLOW_SCENARIOS) for k in per[SLOW_SCENARIOS[0]]}
        out[name] = dict(agg=agg, per=per)
        print(f"{name:26} {agg['lost']:6.1f} {agg['events_h']:9.1f} {agg['tv_h']:7.0f} {agg['slow_mean_b']:12.0f} {agg['slow_over1s']:9.1f} {agg['slow_max_b']:12.0f} {agg['stuck']:8.0f}"
              + "   " + " ".join(f"{sc[:4]}:{per[sc]['lost']:.0f}/{per[sc]['events_h']:.0f}/{per[sc]['stuck']:.0f}" for sc in SLOW_SCENARIOS))
    if args.json:
        json.dump(out, open(args.json, 'w'), indent=1)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--seeds', type=int, default=20)
    ap.add_argument('--minutes', type=int, default=20)
    ap.add_argument('--json')
    ap.add_argument('--trace', nargs=2, metavar=('SCENARIO', 'POLICY'))
    ap.add_argument('--scenarios', nargs='*', default=SCENARIOS)
    ap.add_argument('--sweep', action='store_true')
    ap.add_argument('--trend-sweep', action='store_true')
    ap.add_argument('--jitter', type=float, default=1.0, help='multiply all lateness measurement jitter (default 1 = 10-30 ms)')
    args = ap.parse_args()
    global JITTER_SCALE
    JITTER_SCALE = args.jitter
    if args.sweep or args.trend_sweep:
        sweep(args); return
    if args.trace:
        sc, pn = args.trace
        pol = [p for p in POLICIES if p.name == pn][0]
        m, ev, rows = run(sc, pol, 1, args.minutes, trace=True)
        print("t  cap  speed  applied  slow-lag(ms)")
        last = None
        for r in rows:
            key = (r[1] // 3, r[2], r[3] // 3)
            if key != last or r[0] % 15 == 0:
                print("  ".join(str(x) for x in r)); last = key
        for e in ev: print(e)
        print(m)
        return
    results = {}
    for sc in args.scenarios:
        for pol in POLICIES:
            ms = [run(sc, pol, seed, args.minutes)[0] for seed in range(args.seeds)]
            agg = {k: statistics.mean(m[k] for m in ms) for k in ms[0]}
            results[(sc, pol.name)] = agg
    cols = [('lost', 'lost%'), ('below', 'below100%'), ('stuck', 'stuck s'), ('events_h', 'events/h'), ('changes_h', 'chg>=3/h'), ('tv_h', 'TV/h'), ('tv_applied_h', 'TV+dither/h'),
            ('slow_mean_b', 'slow mean lag ms'), ('slow_over1s', 'slow >1s %'), ('slow_max_b', 'slow max lag ms'), ('fails', 'fails'), ('reprobes', 'reprobes')]
    for sc in args.scenarios:
        print(f"\n== {sc}  ({args.seeds} seeds x {args.minutes} min)")
        print(f"  {'policy':10}" + "".join(f"{h:>17}" for _, h in cols))
        for pol in POLICIES:
            a = results[(sc, pol.name)]
            print(f"  {pol.name:10}" + "".join(f"{a[k]:17.1f}" for k, _ in cols))
    if args.json:
        json.dump({f"{k[0]}|{k[1]}": v for k, v in results.items()}, open(args.json, 'w'), indent=1)

if __name__ == '__main__':
    main()
