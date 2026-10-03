using Ami.BroAudio.Data;
using UnityEngine;

namespace Ami.BroAudio.Runtime
{
    /// <summary>
    /// Strategy for selecting the first clip in the array
    /// </summary>
    public class SingleClipStrategy : IClipSelectionStrategy
    {
        public IBroAudioClip SelectClip(BroAudioClip[] clips, ClipSelectionContext context, out int index)
        {
            index = 0;
            if (Utility.ClipListIsNullOrEmpty(clips, context.EntityName))
            {
                return null;
            }
            if (clips[0] == null)
            {
                Debug.LogError(Utility.LogTitle + $"`{context.EntityName}`'s first clip is null.");
                return null;
            }
            if (!clips[0].IsSet)
            {
                Debug.LogError(Utility.LogTitle + $"No valid clip is set in [<b>{context.EntityName}</b>]. Please check the clip settings.");
                return null;
            }
            return clips[0];
        }

        public void Reset() { }
    }
}
