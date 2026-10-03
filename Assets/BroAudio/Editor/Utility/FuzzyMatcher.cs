using System;

namespace Ami.BroAudio.Editor
{
    // Hand-rolled because UnityEditor.Search.FuzzySearch doesn't exist at the package's 2020.3 floor.
    public static class FuzzyMatcher
    {
        private const int MatchScore = 1;
        private const int StartBonus = 8;
        private const int WordStartBonus = 6;
        private const int ConsecutiveBonus = 4;
        private const int MaxGapPenalty = 3;

        // ponytail: greedy leftmost alignment, so a later, better-scoring alignment is never tried. Switch to DP if rankings look off.
        public static bool TryMatch(string pattern, string text, out int score)
        {
            score = 0;
            if (string.IsNullOrEmpty(pattern))
            {
                return true;
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int textIndex = 0;
            int prevMatch = -1;
            foreach (char patternChar in pattern)
            {
                char p = char.ToLowerInvariant(patternChar);
                while (textIndex < text.Length && char.ToLowerInvariant(text[textIndex]) != p)
                {
                    textIndex++;
                }

                if (textIndex == text.Length)
                {
                    score = 0;
                    return false;
                }

                score += MatchScore;
                if (textIndex == 0)
                {
                    score += StartBonus;
                }
                else if (IsWordStart(text, textIndex))
                {
                    score += WordStartBonus;
                }

                if (prevMatch >= 0)
                {
                    int gap = textIndex - prevMatch - 1;
                    score += gap == 0 ? ConsecutiveBonus : -Math.Min(gap, MaxGapPenalty);
                }

                prevMatch = textIndex;
                textIndex++;
            }
            return true;
        }

        private static bool IsWordStart(string text, int index)
        {
            char prev = text[index - 1];
            return prev == '_' || prev == '-' || prev == ' ' || (char.IsLower(prev) && char.IsUpper(text[index]));
        }
    }
}