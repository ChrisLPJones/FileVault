namespace Backend.Test
{
    // A clock tests can move forward, so authenticator codes for later time steps can be made
    // without waiting 30 seconds. Starts at the real time, because the database uses its own clock.
    public class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
