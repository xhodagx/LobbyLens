namespace LobbyLens
{
    // One rendered row of the panel.
    public class RankLine
    {
        public string Text;      // left column (name, placeholder, hint)
        public string Sub;       // smaller dim second line (hero · tier · health)
        public string Sub2;      // third line (board comp: "4 Mech · 2 Beast — t9")
        public string Right;     // right column, bold (rating), null = no right column
        public string RightDim;  // dimmed suffix after Right (ladder rank, e.g. "#214")
        public bool Dead;        // strikethrough (eliminated player)
        public bool Dim;         // grayed italic (placeholder / hint)
        public bool Divider;     // thin separator line between duos teams

        public RankLine(string text, bool dead = false, bool dim = false, bool divider = false)
        {
            Text = text;
            Dead = dead;
            Dim = dim;
            Divider = divider;
        }
    }
}
