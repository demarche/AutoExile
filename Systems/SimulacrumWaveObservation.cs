namespace AutoExile.Systems
{
    // Missing memory is unknown, never evidence that a wave (especially wave 15) ended.
    public sealed class SimulacrumWaveObservation
    {
        public bool IsActive { get; private set; }
        public int Wave { get; private set; }
        public DateTime LastUpdatedAt { get; private set; } = DateTime.MinValue;
        public bool IsFresh(DateTime now) => LastUpdatedAt != DateTime.MinValue &&
            now - LastUpdatedAt <= TimeSpan.FromSeconds(10);

        public bool Observe(int? active, int? goodbye, int? wave, DateTime now)
        {
            if (!active.HasValue || !goodbye.HasValue || !wave.HasValue || wave < 0 || wave > 15)
                return false;

            IsActive = active > 0 && goodbye == 0;
            Wave = wave.Value;
            LastUpdatedAt = now;
            return true;
        }
    }
}
