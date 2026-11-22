/*
 * Displays the backend's saved attempt grade in Unity Reports.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 15/11/2026
 * Last Modified: 23/11/2026
 *
 * Description:
 *     Refreshes on selection changes or when the result is saved and discards replies for
 *     sessions that have since been left. Uses the server's points and
 *     evidence so a reconnect presents the same result.
 */

using System;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    public sealed class AssessmentReportsTab
    {
        private readonly TrainingApi api;
        private readonly Action<Task> run;
        private readonly VisualElement panel, breakdown;
        private readonly Button retry;
        private readonly Label result, context;
        private (string Username, string SessionId, bool Scored, bool Finished) selection;
        private object request;

        public AssessmentReportsTab(VisualElement root, TrainingApi api, Action<Task> run)
        {
            this.api = api;
            this.run = run;
            panel = root.Q("reports-assessment-panel");
            breakdown = root.Q("assessment-breakdown");
            retry = root.Q<Button>("assessment-retry");
            result = root.Q<Label>("assessment-result");
            context = root.Q<Label>("assessment-context");
            result.text = "Select a session to view its assessment.";
            retry.clicked += () => this.run(Load());
        }

        /// <summary>Reuse the in-progress report until the attempt ends, when a saved grade is available.</summary>
        public void Render(FullSnapshot snapshot, string username, bool reportsVisible)
        {
            var next = snapshot == null || username == null ? default :
                (username, snapshot.SessionId, TrainingPresenter.IsAssessment(snapshot),
                    TrainingPresenter.IsTerminalSession(snapshot.Session?.Status));
            if (next != selection)
            {
                selection = next;
                request = null;
                breakdown.Clear();
                context.text = "";
                retry.style.display = DisplayStyle.None;
                result.text = selection.Scored ? "" : selection.SessionId == null ?
                    "Select a session to view its assessment." : "This is a practice session.";
            }
            if (selection.Scored && reportsVisible && panel.style.display.value == DisplayStyle.Flex && request == null)
                run(Load());
        }

        private async Task Load()
        {
            if (!selection.Scored) return;
            var id = selection.SessionId;
            var stamp = request = new object();
            retry.style.display = DisplayStyle.None;
            result.text = "Loading assessment…";
            try
            {
                var grade = await api.GetAssessmentAsync(id);
                if (stamp != request) return;
                Present(grade);
            }
            catch (BackendErrorException error) when (error.Error.Code == "ROLE_DENIED")
            {
                if (stamp == request) result.text = "Only the assigned trainee can view this assessment.";
            }
            catch (Exception)
            {
                if (stamp != request) return;
                result.text = "The assessment could not be loaded.";
                retry.style.display = DisplayStyle.Flex;
            }
        }

        private void Present(SessionAssessment grade)
        {
            result.text = TrainingPresenter.AssessmentResult(grade);
            context.text = grade.RubricRevision == null ? grade.Explanation :
                $"{grade.Explanation}  Rubric {grade.RubricRevision} · Pass mark {grade.PassMark}/{grade.Maximum}";
            breakdown.Clear();
            foreach (var failure in grade.AutomaticFailures)
                AddLine($"Automatic fail · {failure.Explanation}", string.Join(", ", failure.EventIds));
            foreach (var criterion in grade.Criteria)
            {
                var points = criterion.Points.HasValue ? $"{criterion.Points:0.##}/{criterion.Maximum}" : "Unavailable";
                AddLine($"{criterion.Label} · {points}\n{criterion.Explanation}", string.Join(", ", criterion.EventIds));
            }
        }

        private void AddLine(string text, string evidence)
        {
            var label = new Label(text) { tooltip = evidence };
            label.AddToClassList("workspace-text");
            breakdown.Add(label);
        }
    }
}
