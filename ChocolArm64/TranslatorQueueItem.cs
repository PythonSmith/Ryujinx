namespace ChocolArm64
{
    struct TranslatorQueueItem
    {
        public long Position { get; private set; }
        public int  Depth    { get; private set; }

        public TranslationPriority    Priority      { get; private set; }
        public TranslationCodeQuality TranslationCq { get; private set; }

        public TranslatorQueueItem(
            long                   position,
            int                    depth,
            TranslationPriority    priority,
            TranslationCodeQuality translationCq)
        {
            Position      = position;
            Depth         = depth;
            Priority      = priority;
            TranslationCq = translationCq;
        }
    }
}