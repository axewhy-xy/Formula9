/*
 * Applies fleet colours to the shuttle bus model.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 03/11/2026
 * Last Modified: 03/11/2026
 *
 * Description:
 *     Applies each shuttle's fleet colour to its configured body material
 *     using MaterialPropertyBlock, without creating separate materials.
 *     Preserves the appearance of windows, wheels and lights, and reports
 *     missing or incorrect material configuration.
 */
using UnityEngine;

namespace Formula9.World
{
    public sealed class ShuttleAppearance : MonoBehaviour
    {
        [SerializeField]
        private Material bodyPaintMaterial;

        public void ApplyFleetColor(Color color)
        {
            if (bodyPaintMaterial == null ||
                !bodyPaintMaterial.HasProperty("_Color"))
            {
                Debug.LogWarning(
                    "Assign a body paint material that supports _Color.",
                    this);
                return;
            }

            var block = new MaterialPropertyBlock();
            bool found = false;

            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;

                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != bodyPaintMaterial)
                        continue;

                    renderer.GetPropertyBlock(block, i);
                    block.SetColor("_Color", color);
                    renderer.SetPropertyBlock(block, i);
                    found = true;
                }
            }

            if (!found)
                Debug.LogWarning(
                    "The assigned body paint material is not used by this bus.",
                    this);
        }
    }
}