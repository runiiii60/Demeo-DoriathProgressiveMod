// ============================================================
//  Doriath (PROGRESSIVE) — DoriathPerksPanel.cs
// ============================================================
//
// Fixed, persistent VR panel ("Panel 2") displaying the description of
// Doriath (PROGRESSIVE) mode and the generic detail of level tiers (1 to
// 10) for all heroes. Separate from the "Active Rules" panel (Panel 1,
// native HouseRules).
//
// Faithfully reproduces the native mechanism of
// HouseRules.Configuration.UI.HouseRulesUiGameVr3 (used natively for the
// "Perks and Leveling-Up" panel): same wait for readiness
// (VrElementCreator.IsReady() + presence of the native "~LeanTween" object
// that serves as the "scene ready" signal), same anchor, same
// parchment-background technique (VrResourceTable.MenuMesh/MenuMaterial)
// dynamically sized to the text length, and same layout (title via
// CreateMenuHeaderText, body via CreateLeftText). Positioned at the same
// spot as the native panel: (7, 41.4, -53), rotation (0, 180, 0).

namespace DoriathMod
{
    using System.Collections;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Common.UI;
    using Common.UI.Element;
    using UnityEngine;

    internal sealed class DoriathPerksPanel : MonoBehaviour
    {
        private const string BodyText =
            "\n\n\n\n" +
            "<b>Welcome to Doriath!</b>\n\n" +
            "Heroes LEVEL UP by filling the mana bar. When knocked down you MUST be picked up by magic, a potion, or a fountain, or you lose a level. Leveling up heals you and revives you if you were downed. Each level unlocks a different ability depending on your hero. If you die and return to pick another character, you will start back at level 1. Party members can check their current level and ability details by grabbing their own character in-game.\n\n" +
            "Doriath is FULLY Progressive — enemies level up too. Their health and attack power scale with a DYNAMIC multiplier that follows the party's AVERAGE level, fully automatic across a run's 3 maps, increasing steadily and proportionally as the heroes level up.\n\n" +
            "Expect MORE enemies than the base game — both while exploring and Even more during boss fights, which now spawn extra waves alongside the boss itself.\n\n" +
            "The pace is also more relentless: zones fill up faster and difficulty spikes trigger earlier than in the base game, so encounters escalate before you've finished exploring.\n\n" +
            "Each hero's attacks and abilities are tied to their own element: Fire for the Sorcerer, Ice for the Warlock, Lightning for the Bard, Poison for the Rogue, etc.\n\n\n" +
            "Level 1 - You start with 3 knockdowns before you're downed, and none of your abilities are free yet.\n" +
            "Level 2 - Your starting ability becomes free, and some heroes unlock an extra one.\n" +
            "Level 3 - Critical hits trigger a special effect tied to your class, and you gain another knockdown.\n" +
            "Level 4 - +2 Max Health, and Critical hits grant +20 Gold.\n" +
            "Level 5 - A new class-specific ability is unlocked or upgraded.\n" +
            "Level 6 - A new replenishable ability.\n" +
            "Level 7 - One more knockdown, and a new replenishable ability.\n" +
            "Level 8 - Critical hits heal you, and you gain a permanent stat bonus.\n" +
            "Level 9 - Your class's ultimate ability is unlocked.\n" +
            "Level 10 - Critical hits grant a free class-specific ability.";

        private const string HeaderText = "<color=#FFA500>Perks and Leveling-Up</color>";

        // Same position/rotation as the native "Perks and Leveling-Up" panel
        // (HouseRulesUiGameVr3), to occupy exactly the same physical spot.
        private static readonly Vector3 PanelPosition = new Vector3(7f, 41.4f, -53f);
        private static readonly Quaternion PanelRotation = Quaternion.Euler(0f, 180f, 0f);

        private VrResourceTable _resourceTable;
        private IElementCreator _elementCreator;
        private Transform _anchor;

        private void Start()
        {
            StartCoroutine(WaitAndInitialize());
        }

        private IEnumerator WaitAndInitialize()
        {
            while (!VrElementCreator.IsReady() ||
                   Resources.FindObjectsOfTypeAll<GameObject>().Count(go => go.name == "~LeanTween") < 1)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            _resourceTable = VrResourceTable.Instance();
            _elementCreator = VrElementCreator.Instance();
            _anchor = Resources.FindObjectsOfTypeAll<GameObject>().First(go => go.name == "~LeanTween").transform;

            Initialize();
        }

        private void Initialize()
        {
            transform.SetParent(_anchor, true);
            transform.position = PanelPosition;
            transform.rotation = PanelRotation;

            // ── Dynamic scroll sizing based on text length ──
            // (same formula as HouseRulesUiGameVr3: estimate a "line count" from the
            // length of the visible text (<color> tags excluded) and the number of
            // paragraph breaks, then derive the background scale and the vertical
            // positioning of the title/body from it.)
            string text = BodyText;
            int visibleLength = text.Length - (Regex.Matches(text, "<color=").Count * 23);
            int paragraphBreaks = Regex.Matches(text, "\n\n").Count;
            float lineMetric = 13f + ((visibleLength - 650f) / 65f) + paragraphBreaks;

            var background = new GameObject("Background");
            background.AddComponent<MeshFilter>().mesh = _resourceTable.MenuMesh;
            background.AddComponent<MeshRenderer>().material = _resourceTable.MenuMaterial;
            background.transform.SetParent(transform, false);
            background.transform.localPosition = Vector3.zero;
            background.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            float depthScale = 1.5f + 0.09f * (lineMetric - 11f);
            background.transform.localScale = new Vector3(4.75f, 1f, depthScale);

            var header = _elementCreator.CreateMenuHeaderText(HeaderText);
            header.transform.SetParent(transform, false);
            float headerY = 3.6f + 0.21f * (lineMetric - 11f);
            header.transform.localPosition = new Vector3(0f, headerY, -0.2f);

            var sb = new StringBuilder();
            sb.AppendLine(ColorizeString(text, Color.black));
            var body = _elementCreator.CreateLeftText(sb.ToString());
            body.transform.SetParent(transform, false);
            float bodyY = -0.195f * lineMetric;
            body.transform.localPosition = new Vector3(0f, bodyY, -0.2f);

            gameObject.AddComponent<BoxCollider>();
        }

        private static string ColorizeString(string text, Color color)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
        }
    }
}
