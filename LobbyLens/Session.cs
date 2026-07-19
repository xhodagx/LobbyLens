using System.Collections.Generic;
using System.Linq;

namespace LobbyLens
{
    // Running per-HDT-session BG record: placements always, MMR deltas when known.
    //
    // Delta timing is deferred by design. Your rating on the server only settles a
    // few seconds after a game ends, so reading it at return-to-menu races the update.
    // Instead we read the rating at each game's START (by then the prior game's result
    // has landed) and attribute (thisStart - lastStart) to the previous game. Net
    // effect: placements count immediately, the most recent game's delta fills in when
    // the next game begins. Rating-less lobbies (CN / ratings down) still track places.
    public static class Session
    {
        public class GameResult
        {
            public int Placement;
            public int? RatingDelta;
        }

        private static readonly List<GameResult> Games = new List<GameResult>();
        private static int? _lastStartRating;
        private static int _pendingDeltaIndex = -1;

        public static int Count => Games.Count;

        // At match start: backfill the previous game's delta, then anchor this game.
        public static void OnGameStart(int? currentRating)
        {
            if (_pendingDeltaIndex >= 0 && _lastStartRating.HasValue && currentRating.HasValue)
            {
                Games[_pendingDeltaIndex].RatingDelta = currentRating.Value - _lastStartRating.Value;
                _pendingDeltaIndex = -1;
            }
            if (currentRating.HasValue) { _lastStartRating = currentRating; }
        }

        // At return-to-menu: record the placement now; its delta backfills next start.
        public static void OnGameEnd(int placement)
        {
            if (placement <= 0) { return; }
            Games.Add(new GameResult { Placement = placement });
            _pendingDeltaIndex = Games.Count - 1;
        }

        // Compact one-liner for the panel header, or null when nothing's tracked yet.
        public static string Summary()
        {
            if (Games.Count == 0) { return null; }
            double avg = Games.Average(g => g.Placement);
            string s = $"Session: {Games.Count} game{(Games.Count == 1 ? "" : "s")} · avg {avg:F1}";
            var deltas = Games.Where(g => g.RatingDelta.HasValue).ToList();
            if (deltas.Count > 0)
            {
                int net = deltas.Sum(g => g.RatingDelta.Value);
                s = $"Session: {(net >= 0 ? "+" : "")}{net} · {Games.Count} game{(Games.Count == 1 ? "" : "s")} · avg {avg:F1}";
            }
            return s;
        }
    }
}
