using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    // The Charging page: pick a shuttle, choose what to do, check it with the backend, apply it, and
    // follow the booking. Only the operator's draft lives here. Everything on the schedule that is
    // not labelled draft came from the backend, and a draft reserves nothing until it is applied.
    public sealed class ChargingPage
    {
        private readonly TrainingApi api;
        private readonly SessionController session;
        private readonly Action<Task> run;
        private readonly Action<string> notify;
        private readonly Func<bool> current;

        private readonly VisualElement root, lockBanner, stages, kpis, queue, choices, impact, timeline, exceptions, bookings, issues;
        private readonly Label lockText, decisionTitle, clock, verdict, assumptions, draftLine;
        private readonly TextField reason;
        private readonly Button apply, liveView, draftView, earlier, later;
        private readonly DropdownField draftCharger;
        private readonly VisualElement draftControls;

        private string vehicleId, choice, replacing, draftChargerId;
        private long draftStart, previewGeneration;
        private bool showDraft = true, dragging, busy;
        private ChargeSchedulePreview preview;
        private string previewKey, queueKey, choicesKey, bookingsKey, exceptionsKey, kpiKey, timelineKey, impactKey;
        private readonly IVisualElementScheduledItem previewTimer;
        private const long PreviewSettleMs = 150, RecheckTicks = 200;
        private const float RowHeight = 40;

        public ChargingPage(VisualElement root, TrainingApi api, SessionController session, Action<Task> run, Action<string> notify, Func<bool> current)
        {
            this.root = root; this.api = api; this.session = session; this.run = run; this.notify = notify; this.current = current;
            lockBanner = root.Q("charge-lock"); lockText = root.Q<Label>("charge-lock-text");
            stages = root.Q("charge-stages"); kpis = root.Q("charge-kpis"); choices = root.Q("charge-choices"); impact = root.Q("charge-impact");
            timeline = root.Q("charge-timeline"); exceptions = root.Q("charge-exceptions"); issues = root.Q("charge-issues");
            queue = TrainingWorkspace.Table(root.Q("charge-queue-host"), "", "Shuttle", "Battery", "Loops", "Urgency");
            bookings = TrainingWorkspace.Table(root.Q("charge-bookings-host"), "", "Shuttle", "Charger", "Slot", "Status", "Actions");
            decisionTitle = root.Q<Label>("charge-decision-title"); clock = root.Q<Label>("charge-clock");
            verdict = root.Q<Label>("charge-verdict"); assumptions = root.Q<Label>("charge-assumptions"); draftLine = root.Q<Label>("charge-draft-line");
            reason = root.Q<TextField>("charge-reason");
            reason.RegisterValueChangedCallback(_ => UpdateActions());
            apply = root.Q<Button>("charge-apply"); apply.clicked += () => run(Apply());
            liveView = root.Q<Button>("charge-view-live"); liveView.clicked += () => { showDraft = false; Refresh(); };
            draftView = root.Q<Button>("charge-view-draft"); draftView.clicked += () => { showDraft = true; Refresh(); };
            // The same moves as dragging, without a pointer
            draftControls = root.Q("charge-draft-controls");
            earlier = root.Q<Button>("charge-earlier"); earlier.clicked += () => MoveDraft(draftChargerId, draftStart - 30 * ChargingPresenter.TicksPerSecond);
            later = root.Q<Button>("charge-later"); later.clicked += () => MoveDraft(draftChargerId, draftStart + 30 * ChargingPresenter.TicksPerSecond);
            draftCharger = root.Q<DropdownField>("charge-draft-charger");
            draftCharger.RegisterValueChangedCallback(e => { if (e.newValue != draftChargerId && !string.IsNullOrEmpty(e.newValue)) MoveDraft(e.newValue, draftStart); });
            foreach (var stage in new[] { "1 Select", "2 Decide", "3 Check", "4 Apply" })
            {
                var label = new Label(stage); label.AddToClassList("route-stage"); stages.Add(label);
            }
            previewTimer = root.schedule.Execute(() => run(CheckDraft())); previewTimer.Pause();
        }

        // Another session: its shuttles, reason and draft are not this one's.
        public void Reset()
        {
            vehicleId = null; ClearChoice(); reason.value = "";
            queueKey = choicesKey = bookingsKey = exceptionsKey = kpiKey = timelineKey = impactKey = null;
        }

        private FullSnapshot Snapshot => session.Store.Snapshot;
        private bool Scheduling => choice == "SCHEDULE";

        private void Refresh() { Render(Snapshot); UpdateActions(); }

        public void Render(FullSnapshot s)
        {
            var locked = ChargingPresenter.Locked(s);
            lockBanner.style.display = locked == null ? DisplayStyle.None : DisplayStyle.Flex;
            lockText.text = locked ?? "";
            stages.style.display = locked == null ? DisplayStyle.Flex : DisplayStyle.None;  // the lock takes its place, so nothing below moves
            if (s == null) return;
            if (vehicleId == null || s.Vehicles.All(v => v.VehicleId != vehicleId))
                vehicleId = ChargingPresenter.Queue(s).FirstOrDefault()?.VehicleId;
            var offered = ChargingPresenter.Choices(s, vehicleId);
            if (choice != null && offered.All(c => c.Kind != choice || !c.Available)) ClearChoice();
            clock.text = $"Simulation time {ChargingPresenter.SimClock(s.Tick)}";
            RenderKpis(s); RenderQueue(s); RenderChoices(s, offered); RenderSchedule(s); RenderBookings(s); RenderExceptions(s);
            RecheckIfStale(s);
            RenderCheck(s);
            UpdateActions();
        }

        private void RenderKpis(FullSnapshot s)
        {
            var values = ChargingPresenter.Kpis(s);
            var key = string.Join("|", values.Select(k => k.Value + k.Note + k.Tone));
            if (key == kpiKey) return;
            kpiKey = key; kpis.Clear();
            foreach (var kpi in values)
            {
                var card = new VisualElement(); card.AddToClassList("charge-kpi"); card.AddToClassList("charge-tone-" + kpi.Tone);
                card.tooltip = kpi.Note;
                card.Add(Styled(kpi.Value, "charge-kpi-value")); card.Add(Styled(kpi.Title, "charge-kpi-title"));
                kpis.Add(card);
            }
        }

        private void RenderQueue(FullSnapshot s)
        {
            var rows = ChargingPresenter.Queue(s);
            var key = vehicleId + string.Concat(rows.Select(r => $"{r.VehicleId}{r.Battery}{r.Loops}{r.State}{r.Eta}{r.Booking}{r.Urgency}"));
            if (key == queueKey) return;
            queueKey = key; queue.Clear();
            foreach (var row in rows)
            {
                var line = new VisualElement(); line.AddToClassList("row"); line.AddToClassList("charge-queue-row");
                line.EnableInClassList("charge-queue-selected", row.VehicleId == vehicleId);
                line.tooltip = $"{row.State} · depot in {row.Eta} · booking: {row.Booking}";
                var name = new Label(row.VehicleId); name.AddToClassList("data-cell"); name.AddToClassList("charge-queue-name"); line.Add(name);
                var battery = new VisualElement(); battery.AddToClassList("data-cell"); battery.AddToClassList("charge-battery"); line.Add(battery);
                var gauge = new VisualElement(); gauge.AddToClassList("charge-battery-gauge"); battery.Add(gauge);
                var fill = new VisualElement(); fill.AddToClassList("charge-battery-fill"); fill.AddToClassList(ChargingPresenter.BatteryClass(row.BatteryPercent));
                fill.style.width = Length.Percent((float)row.BatteryPercent); gauge.Add(fill);
                battery.Add(Styled($"{row.BatteryPercent:0}%", "charge-battery-text"));
                var loops = new Label(row.Loops); loops.AddToClassList("data-cell"); line.Add(loops);
                var urgency = new VisualElement(); urgency.AddToClassList("data-cell"); line.Add(urgency);
                var chip = Styled(row.Urgency, "charge-chip"); chip.AddToClassList(row.UrgencyClass); urgency.Add(chip);
                // On press, since a refresh between press and release would replace the row
                var id = row.VehicleId;
                line.RegisterCallback<PointerDownEvent>(_ => SelectVehicle(id));
                queue.Add(line);
            }
        }

        public void SelectVehicle(string id)
        {
            if (id == vehicleId) return;
            vehicleId = id; ClearChoice(); Refresh();
        }

        private void ClearChoice()
        {
            choice = null; replacing = null; draftChargerId = null; preview = null; previewKey = null; previewGeneration++;
            previewTimer.Pause();
        }

        private void RenderChoices(FullSnapshot s, List<ChargingChoice> offered)
        {
            decisionTitle.text = vehicleId == null ? "Decision" : $"Decision — {vehicleId}";
            var locked = ChargingPresenter.Locked(s) != null;
            var key = $"{vehicleId}|{choice}|{locked}|" + string.Concat(offered.Select(c => c.Kind + c.Detail + c.Available + (c.Option?.Revision ?? 0)));
            if (key == choicesKey) return;
            choicesKey = key; choices.Clear();
            foreach (var option in offered)
            {
                var button = new Button { tooltip = option.Detail }; button.AddToClassList("charge-choice");
                button.EnableInClassList("charge-choice-selected", option.Kind == choice);
                button.Add(Styled(option.Title, "charge-choice-title")); button.Add(Styled(option.Detail, "charge-choice-detail"));
                button.SetEnabled(option.Available && !locked);
                var kind = option.Kind;
                button.clicked += () => Choose(kind);
                choices.Add(button);
            }
            if (offered.Count == 1)
                choices.Add(Styled("Charge now and Continue appear once a shuttle is on its last loops.", "charge-hint"));
        }

        private void Choose(string kind)
        {
            var s = Snapshot;
            ClearChoice(); choice = kind;
            if (kind == "SCHEDULE")
            {
                showDraft = true;
                var existing = s.ChargeBookings.FirstOrDefault(b => b.VehicleId == vehicleId && b.Status == "SCHEDULED");
                if (existing != null) { BeginMove(existing); return; }
                var (charger, start) = ChargingPresenter.SuggestSlot(s);
                draftChargerId = charger; draftStart = start;
                QueueCheck();
            }
            Refresh();
        }

        private void BeginMove(ChargeBooking booking)
        {
            vehicleId = booking.VehicleId; choice = "SCHEDULE"; replacing = booking.BookingId; showDraft = true;
            draftChargerId = booking.ChargerId; draftStart = booking.StartTick; preview = null;
            QueueCheck(); Refresh();
        }

        private void MoveDraft(string charger, long start)
        {
            var s = Snapshot;
            if (s == null || !Scheduling) return;
            draftChargerId = charger;
            draftStart = Math.Max(ChargingPresenter.Snap(s.Tick), ChargingPresenter.Snap(start));
            preview = null; QueueCheck(); Refresh();
        }

        private void QueueCheck()
        {
            previewGeneration++;
            previewKey = null;
            previewTimer.ExecuteLater(PreviewSettleMs);
        }

        // The answer goes stale as the session moves: recheck when a charger or the shuttle changes,
        // and every so often for the battery it will have used by then.
        private string DraftKey(FullSnapshot s) => Scheduling && draftChargerId != null
            ? string.Join("/", s.SessionEpoch, s.Session.Status, s.Tick / RecheckTicks, string.Join(",", s.Chargers.Select(c => c.AggregateRevision)),
                s.Vehicles.FirstOrDefault(v => v.VehicleId == vehicleId)?.AggregateRevision, draftChargerId, draftStart, replacing)
            : null;

        private void RecheckIfStale(FullSnapshot s)
        {
            if (dragging || !Scheduling || draftChargerId == null || busy) return;
            if (previewKey != null && previewKey != DraftKey(s)) run(CheckDraft());
        }

        private async Task CheckDraft()
        {
            previewTimer.Pause();
            var s = Snapshot;
            if (s == null || !Scheduling || draftChargerId == null || dragging || ChargingPresenter.Locked(s) != null) return;
            var generation = ++previewGeneration;
            previewKey = DraftKey(s);
            var request = new ChargeScheduleRequest { VehicleId = vehicleId, ChargerId = draftChargerId, StartTick = draftStart, ReplacesBookingId = replacing };
            try
            {
                var answer = await api.PreviewChargeScheduleAsync(s.SessionId, request);
                if (generation != previewGeneration) return;
                preview = answer;
            }
            catch (Exception error)
            {
                if (generation != previewGeneration) return;
                preview = null;
                notify(error.Message);
            }
            Refresh();
        }

        private void RenderSchedule(FullSnapshot s)
        {
            liveView.EnableInClassList("segment-selected", !showDraft);
            draftView.EnableInClassList("segment-selected", showDraft);
            draftControls.style.display = Scheduling && showDraft ? DisplayStyle.Flex : DisplayStyle.None;
            if (Scheduling && draftChargerId != null)
            {
                draftLine.text = $"{vehicleId} · {draftChargerId} · starts {ChargingPresenter.SimClock(draftStart)}" + (replacing != null ? " (moving its booking)" : "");
                var names = s.Chargers.Select(c => c.ChargerId).OrderBy(c => c, StringComparer.Ordinal).ToList();
                if (!draftCharger.choices.SequenceEqual(names)) draftCharger.choices = names;
                draftCharger.SetValueWithoutNotify(draftChargerId);
            }
            if (dragging) return;  // a rebuild would take the block out from under the pointer
            var blocks = ChargingPresenter.Blocks(s);
            if (replacing != null && showDraft) blocks.RemoveAll(b => b.BookingId == replacing);
            var draft = DraftBlock(s);
            if (draft != null && showDraft) blocks.Add(draft);
            var windowStart = ChargingPresenter.WindowStart(s.Tick);
            var key = windowStart + "|" + s.Tick / 20 + "|" + string.Concat(blocks.Select(b => $"{b.ChargerId}{b.Label}{b.Kind}{b.StartTick}{b.EndTick};"));
            if (key == timelineKey) return;
            timelineKey = key;
            BuildTimeline(s, blocks, windowStart);
        }

        private ChargingBlock DraftBlock(FullSnapshot s)
        {
            if (Scheduling && draftChargerId != null)
            {
                var end = preview != null && preview.ChargerId == draftChargerId && preview.StartTick == draftStart
                    ? preview.EndTick : draftStart + 5 * 60 * ChargingPresenter.TicksPerSecond;
                return new ChargingBlock { Kind = "DRAFT", ChargerId = draftChargerId, StartTick = draftStart, EndTick = end, Label = $"{vehicleId} (draft)" };
            }
            var option = SelectedOption(s);
            if (option?.ChargerId == null) return null;
            return new ChargingBlock { Kind = "DRAFT", ChargerId = option.ChargerId, StartTick = s.Tick,
                EndTick = s.Tick + (long)Math.Ceiling(option.OutOfServiceS * ChargingPresenter.TicksPerSecond) + 1, Label = $"{vehicleId} (draft)" };
        }

        private ChargeOption SelectedOption(FullSnapshot s) => choice == null || Scheduling ? null
            : s.ChargeOptions.FirstOrDefault(o => o.VehicleId == vehicleId && o.Action == choice);

        private void BuildTimeline(FullSnapshot s, List<ChargingBlock> blocks, long windowStart)
        {
            timeline.Clear();
            float Percent(long tick) => 100f * (tick - windowStart) / ChargingPresenter.WindowTicks;
            var header = new VisualElement(); header.AddToClassList("charge-time-row");
            header.Add(Styled("", "charge-track-label"));
            var marks = new VisualElement(); marks.AddToClassList("charge-track"); marks.AddToClassList("charge-track-marks"); header.Add(marks);
            for (long t = windowStart; t < windowStart + ChargingPresenter.WindowTicks; t += 5 * 60 * ChargingPresenter.TicksPerSecond)
            {
                var mark = Styled(ChargingPresenter.SimClock(t), "charge-time-mark"); mark.style.left = Length.Percent(Percent(t)); marks.Add(mark);
            }
            timeline.Add(header);
            var tracks = new Dictionary<string, VisualElement>();
            foreach (var charger in s.Chargers.OrderBy(c => c.ChargerId, StringComparer.Ordinal))
            {
                var row = new VisualElement(); row.AddToClassList("charge-time-row");
                row.Add(Styled($"{charger.ChargerId} · {charger.PowerKw:0} kW", "charge-track-label"));
                var track = new VisualElement(); track.AddToClassList("charge-track"); track.name = "track-" + charger.ChargerId; row.Add(track);
                var now = new VisualElement(); now.AddToClassList("charge-now"); now.style.left = Length.Percent(Percent(s.Tick)); now.pickingMode = PickingMode.Ignore; track.Add(now);
                tracks[charger.ChargerId] = track;
                timeline.Add(row);
            }
            foreach (var b in blocks)
            {
                if (!tracks.TryGetValue(b.ChargerId, out var track)) continue;
                var left = Math.Max(0, Percent(b.StartTick));
                var right = Math.Min(100, Percent(b.EndTick));
                if (right <= left) continue;
                var block = new VisualElement(); block.AddToClassList("charge-block"); block.AddToClassList("charge-block-" + b.Kind.ToLowerInvariant());
                block.style.left = Length.Percent(left); block.style.width = Length.Percent(right - left);
                block.tooltip = $"{b.Label}: {ChargingPresenter.SimClock(b.StartTick)} – {ChargingPresenter.SimClock(b.EndTick)}";
                block.Add(Styled(b.Label, "charge-block-label"));
                track.Add(block);
                if (b.Kind == "DRAFT" && Scheduling) MakeDraggable(block, s);
            }
        }

        private void MakeDraggable(VisualElement block, FullSnapshot s)
        {
            block.AddToClassList("charge-block-movable");
            var windowStart = ChargingPresenter.WindowStart(s.Tick);
            var grabTicks = 0L;
            var pointer = -1;
            string targetCharger = draftChargerId;
            var targetStart = draftStart;
            var ghost = new VisualElement { pickingMode = PickingMode.Ignore };
            ghost.AddToClassList("charge-block"); ghost.AddToClassList("charge-block-draft"); ghost.AddToClassList("charge-block-ghost");
            ghost.Add(Styled($"{vehicleId} (draft)", "charge-block-label"));
            block.RegisterCallback<PointerDownEvent>(e =>
            {
                if (ChargingPresenter.Locked(Snapshot) != null || block.parent == null) return;
                pointer = e.pointerId; dragging = true; targetCharger = draftChargerId; targetStart = draftStart;
                grabTicks = (long)(e.localPosition.x / Math.Max(1f, block.parent.layout.width) * ChargingPresenter.WindowTicks);
                block.CapturePointer(pointer); block.AddToClassList("charge-block-dragging"); timeline.Add(ghost); e.StopPropagation();
            });
            block.RegisterCallback<PointerMoveEvent>(e =>
            {
                // Also fires when the mouse just passes over the block; -1 is not a valid pointer to ask about
                if (pointer < 0 || !block.HasPointerCapture(pointer) || block.parent == null) return;
                var track = block.parent;
                var position = new Vector2(e.position.x, e.position.y);
                var x = position.x - track.worldBound.x;
                var tick = windowStart + (long)(x / Math.Max(1f, track.layout.width) * ChargingPresenter.WindowTicks) - grabTicks;
                targetStart = Math.Max(ChargingPresenter.Snap(Snapshot.Tick), ChargingPresenter.Snap(tick));
                var over = timeline.Query(className: "charge-track").ToList().FirstOrDefault(t => t.name.StartsWith("track-") && position.y >= t.worldBound.yMin && position.y <= t.worldBound.yMax);
                if (over != null) targetCharger = over.name.Substring("track-".Length);
                // A copy over every row shows it; the real block must stay put to keep the pointer
                var shown = over ?? track;
                var corner = timeline.WorldToLocal(new Vector2(shown.worldBound.x + (targetStart - windowStart) * shown.worldBound.width / ChargingPresenter.WindowTicks, shown.worldBound.y));
                ghost.style.left = corner.x; ghost.style.top = corner.y + 3; ghost.style.width = block.worldBound.width; ghost.style.height = shown.worldBound.height - 6;
                draftCharger.SetValueWithoutNotify(targetCharger);
                draftLine.text = $"{vehicleId} · {targetCharger} · starts {ChargingPresenter.SimClock(targetStart)}";
            });
            void Finish()
            {
                if (!dragging) return;
                dragging = false;
                if (pointer >= 0 && block.HasPointerCapture(pointer)) block.ReleasePointer(pointer);
                pointer = -1;
                ghost.RemoveFromHierarchy();
                timelineKey = null;
                MoveDraft(targetCharger, targetStart);
            }
            block.RegisterCallback<PointerUpEvent>(_ => Finish());
            block.RegisterCallback<PointerCaptureOutEvent>(_ => Finish());
        }

        private void RenderBookings(FullSnapshot s)
        {
            var rows = ChargingPresenter.Bookings(s);
            var locked = ChargingPresenter.Locked(s) != null;
            var key = locked + string.Concat(rows.Select(r => $"{r.Booking.BookingId}{r.Booking.Revision}{r.Status}{r.Slot};"));  // Status carries the phase
            if (key == bookingsKey) return;
            bookingsKey = key; bookings.Clear();
            foreach (var row in rows)
            {
                var line = new VisualElement(); line.AddToClassList("row"); bookings.Add(line);
                line.tooltip = row.Source;
                foreach (var text in new[] { row.Shuttle, row.Charger, row.Slot })
                {
                    var cell = new Label(text) { tooltip = text }; cell.AddToClassList("data-cell"); line.Add(cell);
                }
                var status = new VisualElement(); status.AddToClassList("data-cell"); line.Add(status);
                var statusChip = Styled(row.Status, "charge-chip"); statusChip.AddToClassList(ChargingPresenter.StatusClass(row.Status)); status.Add(statusChip);
                var actions = new VisualElement(); actions.AddToClassList("data-cell"); actions.AddToClassList("charge-actions"); line.Add(actions);
                if (row.Movable && !locked)
                {
                    var booking = row.Booking;
                    actions.Add(new Button(() => BeginMove(booking)) { text = "Move", tooltip = "Move this booking on the schedule." });
                    var cancel = new Button(() => run(Cancel(booking))) { text = "Cancel", tooltip = "Cancel this booking, using the reason below.", userData = "cancel" };
                    actions.Add(cancel);
                }
                else actions.Add(new Label("—"));
            }
        }

        private void RenderExceptions(FullSnapshot s)
        {
            var values = ChargingPresenter.Exceptions(s);
            var key = string.Concat(values.Select(e => e.Text));
            if (key == exceptionsKey) return;
            exceptionsKey = key; exceptions.Clear();
            if (values.Count == 0) exceptions.Add(Styled("Nothing needs attention.", "charge-hint"));
            foreach (var item in values)
            {
                var line = Styled(item.Text, "charge-exception");
                line.EnableInClassList("charge-exception-severe", item.Severe);
                var id = item.VehicleId;
                line.RegisterCallback<PointerDownEvent>(_ => SelectVehicle(id));
                exceptions.Add(line);
            }
        }

        private void RenderCheck(FullSnapshot s)
        {
            var option = SelectedOption(s);
            var rows = ChargingPresenter.Impact(s, vehicleId, option, Scheduling ? preview : null);
            var impactNow = string.Concat(rows.Select(r => r.Label + r.Value));
            if (impactNow != impactKey)
            {
                impactKey = impactNow; impact.Clear();
                if (rows.Count == 0) impact.Add(Styled("Choose a decision to see its effect.", "charge-hint"));
                foreach (var row in rows)
                {
                    var line = new VisualElement(); line.AddToClassList("charge-impact-row");
                    line.Add(Styled(row.Label, "charge-impact-label"));
                    var value = Styled(row.Value, "charge-impact-value"); value.EnableInClassList("charge-impact-worse", row.Worse); line.Add(value);
                    impact.Add(line);
                }
            }
            issues.Clear();
            verdict.EnableInClassList("route-verdict-good", false); verdict.EnableInClassList("route-verdict-bad", false);
            if (choice == null) { verdict.text = "Nothing to check yet."; assumptions.text = ""; }
            else if (Scheduling)
            {
                if (preview == null) { verdict.text = "Checking this slot…"; assumptions.text = ""; }
                else
                {
                    verdict.text = preview.Validation == "FAIL" ? "This slot can't be booked." : preview.Validation == "WARN" ? "Can be booked, with a warning." : "Slot checked: can be booked.";
                    verdict.EnableInClassList("route-verdict-good", preview.Validation != "FAIL");
                    verdict.EnableInClassList("route-verdict-bad", preview.Validation == "FAIL");
                    foreach (var i in preview.Issues) { var line = Styled(i.Message, "route-card-text"); issues.Add(line); }
                    verdict.tooltip = $"Checked at {ChargingPresenter.SimClock(preview.Tick)} · estimates: " + string.Join("; ", preview.Assumptions);
                }
            }
            else if (option != null)
            {
                verdict.text = option.Validation == "PASS" ? "Backend offer is valid." : "Backend offer can't be used.";
                verdict.EnableInClassList("route-verdict-good", option.Validation == "PASS");
                verdict.EnableInClassList("route-verdict-bad", option.Validation != "PASS");
                verdict.tooltip = "Estimates: " + string.Join("; ", option.Assumptions);
            }
            var ready = ReadyToApply(s, out _);
            var stage = ChargingPresenter.Stage(vehicleId, choice, ready);
            for (var i = 0; i < stages.childCount; i++)
            {
                stages[i].EnableInClassList("route-stage-current", i == stage);
                stages[i].EnableInClassList("route-stage-done", i < stage);
            }
        }

        // Everything but the reason: the choice is current and the backend's latest answer allows it.
        private bool ReadyToApply(FullSnapshot s, out string why)
        {
            why = null;
            if (s == null || ChargingPresenter.Locked(s) != null) { why = ChargingPresenter.Locked(s); return false; }
            if (choice == null) { why = "Choose a decision first."; return false; }
            if (Scheduling)
            {
                if (preview == null || dragging) { why = "Waiting for the slot check."; return false; }
                if (preview.Validation == "FAIL") { why = "Fix the problem above."; return false; }
                if (preview.ChargerId != draftChargerId || preview.StartTick != draftStart || preview.ReplacesBookingId != replacing
                    || s.Chargers.FirstOrDefault(c => c.ChargerId == draftChargerId)?.AggregateRevision != preview.ChargerRevision
                    || draftStart < s.Tick)
                { why = "The session moved on. Rechecking."; return false; }
                return true;
            }
            var option = SelectedOption(s);
            if (option == null || option.Validation != "PASS" || option.ExpiresAtTick < s.Tick) { why = "This offer has ended."; return false; }
            return true;
        }

        public void UpdateActions()
        {
            var s = Snapshot;
            var ready = ReadyToApply(s, out var why);
            var hasReason = !string.IsNullOrWhiteSpace(reason.value);
            var usable = current() && !busy;
            apply.SetEnabled(ready && hasReason && usable);
            apply.text = !Scheduling ? "Apply decision" : replacing != null ? "Apply: move booking" : "Apply: book slot";
            apply.tooltip = why ?? (hasReason ? "" : "Enter a reason first.");
            var locked = ChargingPresenter.Locked(s) != null;
            reason.SetEnabled(!locked);
            earlier.SetEnabled(!locked); later.SetEnabled(!locked); draftCharger.SetEnabled(!locked);
            foreach (var cancel in bookings.Query<Button>().ToList().Where(b => (string)b.userData == "cancel")) cancel.SetEnabled(hasReason && usable);
        }

        private async Task Apply()
        {
            var s = Snapshot;
            if (!ReadyToApply(s, out _) || busy || !current()) return;
            busy = true; UpdateActions();
            try
            {
                var command = Scheduling
                    ? DecisionRules.ScheduleChargeCommand(s, vehicleId, draftChargerId, draftStart, replacing, reason.value)
                    : DecisionRules.ChargeCommand(s, SelectedOption(s).OptionId, reason.value);
                if (await session.SubmitAsync(command))
                {
                    await session.RefreshAsync();
                    reason.value = ""; ClearChoice();
                }
            }
            finally { busy = false; Refresh(); }
        }

        private async Task Cancel(ChargeBooking booking)
        {
            var s = Snapshot;
            if (s == null || busy || !current() || string.IsNullOrWhiteSpace(reason.value)) return;
            busy = true; UpdateActions();
            try
            {
                if (await session.SubmitAsync(DecisionRules.CancelChargeBookingCommand(s, booking.BookingId, reason.value)))
                {
                    await session.RefreshAsync();
                    reason.value = "";
                    if (replacing == booking.BookingId) ClearChoice();
                }
            }
            finally { busy = false; Refresh(); }
        }

        private static Label Styled(string text, string css) { var label = new Label(text); label.AddToClassList(css); return label; }
    }
}
