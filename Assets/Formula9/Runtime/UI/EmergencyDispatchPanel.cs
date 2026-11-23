/*
 * The Emergency dispatch block on the Incident Monitor page.
 *
 * Description:
 *     Lists the shuttles that cannot run and lets the operator ask for assistance for one. The
 *     request is the backend's own "request assistance" incident option, approved with a reason
 *     like any other incident decision, so it is recorded and replayed with the rest of the
 *     session. The button stays disabled while the backend offers no such option; the rules are in
 *     DecisionRules.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Contracts;
using Formula9.Session;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    // -----------------------------------------------------------------------------------
    // Class: EmergencyDispatchPanel
    // -----------------------------------------------------------------------------------
    // Description:
    //     Owns the controls named emergency in IncidentMonitor.uxml. TrainingWorkspace creates it
    //     once and calls Render for every snapshot and UpdateActions on its refresh timer. It holds
    //     no session data of its own: what it shows is read from the latest snapshot each time.
    public sealed class EmergencyDispatchPanel
    {
        // The session, for the latest snapshot and for sending the command
        private readonly SessionController session;
        // The workspace's runner, which shows a failed command to the operator instead of losing it
        private readonly Action<Task> run;
        // The workspace's message bar
        private readonly Action<string> notify;
        // True while the workspace accepts decisions (its snapshot is fresh and no command is pending)
        private readonly Func<bool> current;
        // The controls from the layout; all null when the layout has no emergency block
        private readonly VisualElement host;
        private readonly DropdownField vehicle;
        private readonly Label status;
        private readonly TextField reason;
        private readonly Button dispatch;
        // True from the click until the backend has answered, so a double click sends one command
        private bool busy;

        // -----------------------------------------------------------------------------------
        // Function: EmergencyDispatchPanel (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     Finds the controls and connects them. If the layout has no emergency block the panel
        //     stays inert and every other method returns at once.
        //
        // Args:
        //     root (VisualElement): The page layout to search.
        //     session (SessionController): The session whose snapshot and commands are used.
        //     run (Action<Task>): Runs a command task and reports its failure.
        //     notify (Action<string>): Shows a message to the operator.
        //     current (Func<bool>): Whether the workspace accepts decisions right now.
        public EmergencyDispatchPanel(VisualElement root, SessionController session, Action<Task> run, Action<string> notify, Func<bool> current)
        {
            this.session = session; this.run = run; this.notify = notify; this.current = current;
            host = root.Q("emergency-dispatch");
            // An older layout without the block: leave everything null and do nothing
            if (host == null) return;
            vehicle = root.Q<DropdownField>("emergency-vehicle");
            status = root.Q<Label>("emergency-status");
            reason = root.Q<TextField>("emergency-reason");
            dispatch = root.Q<Button>("emergency-dispatch-button");
            // The button depends on both the chosen shuttle and the reason, so either change re-checks it
            vehicle.RegisterValueChangedCallback(_ => UpdateActions());
            reason.RegisterValueChangedCallback(_ => UpdateActions());
            dispatch.clicked += () => this.run(Dispatch());
        }

        // -----------------------------------------------------------------------------------
        // Function: Reset
        // -----------------------------------------------------------------------------------
        // Description:
        //     Clears the typed reason when a new session starts, so one session's text does not
        //     carry into the next. SetValueWithoutNotify avoids a pointless UpdateActions.
        public void Reset() { if (host != null) reason.SetValueWithoutNotify(""); }

        // -----------------------------------------------------------------------------------
        // Function: Render
        // -----------------------------------------------------------------------------------
        // Description:
        //     Shows the block while any shuttle cannot run, hides it otherwise, and lists those
        //     shuttles in the dropdown. A missing snapshot (no session joined) lists none, so the
        //     block is hidden.
        //
        // Args:
        //     s (FullSnapshot): The latest snapshot, or null before a session is joined.
        public void Render(FullSnapshot s)
        {
            if (host == null) return;
            var down = DecisionRules.NonOperational(s).Select(v => v.VehicleId).ToList();
            //Purely for debugging purpose ONLY to check for Emergency Dispatch button
            //if (down.Count == 0 && s != null && s.Vehicles.Count > 0) down.Add(s.Vehicles[0].VehicleId);
            host.style.display = down.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            // Keeps the operator's chosen shuttle while it is still in the list
            TrainingWorkspace.Choices(vehicle, down);
            UpdateActions();
        }

        // -----------------------------------------------------------------------------------
        // Function: UpdateActions
        // -----------------------------------------------------------------------------------
        // Description:
        //     Refreshes the status line and the button. The button is enabled only when the backend
        //     offers an assistance option for the chosen shuttle, the operator has typed a reason, no
        //     dispatch is already in flight, and the workspace accepts decisions.
        public void UpdateActions()
        {
            if (host == null) return;
            var s = session.Store.Snapshot;
            var option = s == null || string.IsNullOrEmpty(vehicle.value) ? null : DecisionRules.AssistanceOption(s, vehicle.value);
            status.text = Describe(s, option);
            dispatch.SetEnabled(!busy && current() && option != null && !string.IsNullOrWhiteSpace(reason.value));
        }

        // -----------------------------------------------------------------------------------
        // Function: Describe
        // -----------------------------------------------------------------------------------
        // Description:
        //     The status line under the dropdown: why the button is, or is not, usable.
        //
        // Args:
        //     s (FullSnapshot): The latest snapshot, or null.
        //     option (Candidate): The assistance option for the chosen shuttle, or null when none.
        //
        // Returns:
        //     (string): The line to show, empty when there is nothing to say yet.
        private string Describe(FullSnapshot s, Candidate option)
        {
            if (s == null || string.IsNullOrEmpty(vehicle.value)) return "";
            if (option != null) return "The backend offers assistance for this shuttle. Give a reason to dispatch.";
            // An ESCALATED incident means assistance was already requested for it
            if (s.Incidents?.Any(i => i.VehicleId == vehicle.value && i.State == "ESCALATED") == true) return "Assistance has already been requested for this shuttle.";
            return s.Session.Status == "RUNNING" ? "The backend offers no assistance option for this shuttle yet." : "Emergency dispatch is available while the session is running.";
        }

        // -----------------------------------------------------------------------------------
        // Function: Dispatch
        // -----------------------------------------------------------------------------------
        // Description:
        //     Sends the emergency dispatch: the backend's assistance option for the chosen shuttle,
        //     approved with the typed reason. The command is rebuilt from the snapshot at click time,
        //     because the option may have expired since the screen last refreshed. On success the
        //     reason is cleared and the snapshot refreshed so the incident shows as escalated.
        private async Task Dispatch()
        {
            var s = session.Store.Snapshot;
            if (busy || s == null || !current()) return;
            DispatcherCommand command;
            // No option left, or no reason: tell the operator rather than send a command the backend would refuse
            try { command = DecisionRules.EmergencyDispatchCommand(s, vehicle.value, reason.value); }
            catch (ArgumentException error) { notify(error.Message); return; }
            busy = true; UpdateActions();
            try
            {
                if (await session.SubmitAsync(command)) { reason.SetValueWithoutNotify(""); await session.RefreshAsync(); }
            }
            // Always release the button, whether the backend accepted the command, refused it or failed
            finally { busy = false; UpdateActions(); }
        }
    }
}