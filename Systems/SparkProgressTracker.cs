namespace AutoExile.Systems
{
    public readonly record struct MonsterHealthSample(long Id, double Health);

    // Spawning and leaving the network bubble are not kills. Damage to a living
    // boss still counts as progress, even when no monsters die for several seconds.
    public sealed class SparkProgressTracker
    {
        private Dictionary<long, double> _previous = new();
        private readonly Queue<DateTime> _kills = new();
        public DateTime LastProgressAt { get; private set; }
        public int TotalKills { get; private set; }
        public double NoProgressSeconds(DateTime now) => (now - LastProgressAt).TotalSeconds;
        public float KillRate(DateTime now)
        {
            while (_kills.TryPeek(out var time) && (now - time).TotalSeconds > 2) _kills.Dequeue();
            return _kills.Count / 2f;
        }
        public void Reset(DateTime now)
        {
            _previous.Clear();
            _kills.Clear();
            LastProgressAt = now;
            TotalKills = 0;
        }
        public void Observe(IEnumerable<MonsterHealthSample> samples, DateTime now)
        {
            var current = new Dictionary<long, double>();
            foreach (var sample in samples)
            {
                if (!double.IsFinite(sample.Health) || sample.Health < 0) continue;
                if (!current.TryAdd(sample.Id, sample.Health)) continue;
                if (_previous.TryGetValue(sample.Id, out var previous) && previous > sample.Health)
                {
                    LastProgressAt = now;
                    if (previous > 0 && sample.Health == 0)
                    {
                        TotalKills++;
                        _kills.Enqueue(now);
                    }
                }
            }
            _previous = current;
        }
    }
}
