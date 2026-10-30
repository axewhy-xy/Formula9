using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    // The login screen's background: slow drifting points joined by fading lines over a teal
    // gradient, like the Qt client's plexus shader. It never takes clicks; the form sits on top.
    [UxmlElement]
    public sealed partial class LoginBackdrop : VisualElement
    {
        private const int Points = 30;
        private const float LinkDistance = 0.24f;
        private static readonly Color Light = new Color(0.36f, 0.62f, 0.68f), Dark = new Color(0.12f, 0.29f, 0.36f);
        private static readonly Color Spark = new Color(0.66f, 0.95f, 1f);

        private readonly Vector2[] origins = new Vector2[Points], drift = new Vector2[Points];
        private Texture2D gradient;

        public LoginBackdrop()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = style.top = style.right = style.bottom = 0;
            // Fixed seed: the same constellation every time.
            var random = new System.Random(9);
            for (var i = 0; i < Points; i++)
            {
                origins[i] = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
                drift[i] = new Vector2((float)random.NextDouble() * 6.28f, (float)random.NextDouble() * 6.28f);
            }
            generateVisualContent += Draw;
            // About 30 FPS is plenty for slow drifting points; a hidden element paints nothing.
            schedule.Execute(MarkDirtyRepaint).Every(33);
            RegisterCallback<AttachToPanelEvent>(_ => CreateGradient());
            RegisterCallback<DetachFromPanelEvent>(_ => DestroyGradient());
        }

        private void CreateGradient()
        {
            if (gradient != null) return;
            const int size = 64;
            gradient = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var center = new Vector2(0.68f, 0.55f);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    gradient.SetPixel(x, y, Color.Lerp(Light, Dark, Vector2.Distance(new Vector2((float)x / size, (float)y / size), center) / 0.85f));
            gradient.Apply(false, true);
            style.backgroundImage = new StyleBackground(gradient);
        }

        private void DestroyGradient()
        {
            if (gradient == null) return;
            style.backgroundImage = StyleKeyword.Null;
            if (Application.isPlaying) Object.Destroy(gradient);
            else Object.DestroyImmediate(gradient);
            gradient = null;
        }

        private Vector2 Point(int index, float time) => new Vector2(
            origins[index].x + Mathf.Sin(time * 0.05f + drift[index].x) * 0.03f,
            origins[index].y + Mathf.Cos(time * 0.04f + drift[index].y) * 0.03f);

        private void Draw(MeshGenerationContext mesh)
        {
            var size = contentRect.size;
            if (size.x < 1 || size.y < 1) return;
            var painter = mesh.painter2D;
            var time = Time.realtimeSinceStartup;
            painter.lineWidth = 1;
            for (var a = 0; a < Points; a++)
                for (var b = a + 1; b < Points; b++)
                {
                    var from = Point(a, time); var to = Point(b, time);
                    var distance = Vector2.Distance(from, to);
                    if (distance >= LinkDistance) continue;
                    painter.strokeColor = new Color(Spark.r, Spark.g, Spark.b, 0.4f * (1 - distance / LinkDistance));
                    painter.BeginPath(); painter.MoveTo(Vector2.Scale(from, size)); painter.LineTo(Vector2.Scale(to, size)); painter.Stroke();
                }
            for (var i = 0; i < Points; i++)
            {
                var at = Vector2.Scale(Point(i, time), size);
                foreach (var (radius, alpha) in new[] { (9f, 0.08f), (5f, 0.16f), (2f, 1f) })
                {
                    painter.fillColor = new Color(Spark.r, Spark.g, Spark.b, alpha);
                    painter.BeginPath(); painter.Arc(at, radius, 0, 360); painter.Fill();
                }
            }
        }
    }
}
