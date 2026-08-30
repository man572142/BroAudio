namespace Ami.Extension
{
	public static class FlagsExtension
	{
        public enum FlagsRangeType
        {
            Included,
            Excluded,
        }

        public static bool ContainsFlag(this int flags, int targetFlag)
        {
            return (flags & targetFlag) != 0;
        }

        public static void AddFlag(ref int flags, int add)
        {
            flags |= add;
        }

        public static void RemoveFlag(ref int flags, int remove)
        {
            flags &= ~remove;
        }

        public static void OverwriteFlag(ref int flags, int targetFlag, int overwriteValue)
        {
            flags = (flags & ~targetFlag) | overwriteValue;
        }

        public static int GetFlagsOnCount(int flags)
        {
            int count = 0;
            while (flags != 0)
            {
                flags = flags & (flags - 1);
                count++;
                if(count > 32) // integer has only 32-bit max
                {
                    UnityEngine.Debug.LogError(Ami.BroAudio.Utility.LogTitle + "count flags is failed");
                    break;
				}
            }
            return count;
        }

        public static int GetFlagsRange(int minIndex,int maxIndex, FlagsRangeType rangeType)
        {
            int flagsRange = 0;
            for(int i = minIndex; i <= maxIndex;i++)
            {
                flagsRange += 1 << i;
            }

            return rangeType switch 
            {
                FlagsRangeType.Included => flagsRange,
                FlagsRangeType.Excluded => ~flagsRange,
                _ => default,
            };
        }

        public static int GetFirstFlag(int flags)
        {
            if(flags <= 0)
            {
                return flags;
            }

            int result = 1;
            while ((flags & result) == 0)
            {
                result <<= 1;
            }
            return result;
        }
    }
}