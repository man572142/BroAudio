using Ami.BroAudio.Data;

namespace Ami.BroAudio.Runtime
{
    public class VelocityClipStrategy : IClipSelectionStrategy
    {
        public IBroAudioClip SelectClip(BroAudioClip[] clips, ClipSelectionContext context, out int index)
        {
            index = 0;
            if (Utility.ClipListIsNullOrEmpty(clips, context.EntityName))
            {
                return null;
            }
            
            for(int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                if(clip.Velocity > context.Value)
                {
                    index = i == 0 ? 0 : i - 1;
                    return clips[index];
                }
            }
            index = clips.Length - 1;
            return clips[index];
        }

        public void Reset() { }
    }
}
