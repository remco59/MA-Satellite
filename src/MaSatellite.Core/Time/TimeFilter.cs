namespace MaSatellite.Core.Time;

/// <summary>
/// Two-dimensional Kalman filter tracking the offset (server clock minus client clock) and its drift,
/// exactly as specified by the Sendspin protocol. All times are microseconds.
/// </summary>
public sealed class TimeFilter
{
    private const double ProcessStdDev = 0.0;
    private const double DriftProcessStdDev = 1e-11;
    private const double ForgetFactor = 2.0;
    private const double AdaptiveCutoff = 3.0;
    private const int MinSamples = 100;
    private const double DriftSignificanceThreshold = 2.0;
    private const double MaxErrorScale = 0.5;

    private double _offset;
    private double _drift;
    private double _pOo = double.PositiveInfinity;
    private double _pOd;
    private double _pDd;
    private long _lastUpdate;
    private int _count;
    private bool _useDrift;

    public int Count => _count;

    public double OffsetMicros => _offset;

    public double DriftPerMicro => _drift;

    /// <summary>Standard deviation of the offset estimate in microseconds (infinite before the first sample).</summary>
    public double StdDevMicros => Math.Sqrt(_pOo);

    /// <summary>Feeds one NTP-style exchange. Returns false if the measurement was discarded.</summary>
    public bool Update(long clientTransmitted, long serverReceived, long serverTransmitted, long clientReceived)
    {
        var measurement = (((serverReceived - clientTransmitted) + (serverTransmitted - clientReceived)) / 2);
        var maxError = (((clientReceived - clientTransmitted) - (serverTransmitted - serverReceived)) / 2);
        return Update(measurement, maxError, clientReceived);
    }

    public bool Update(long measurement, long maxError, long timeAdded)
    {
        if (timeAdded <= _lastUpdate && _count > 0 || maxError <= 0)
        {
            return false;
        }

        double dt = timeAdded - _lastUpdate;
        _lastUpdate = timeAdded;
        var r = Math.Pow(MaxErrorScale * maxError, 2);

        if (_count == 0)
        {
            _offset = measurement;
            _pOo = r;
            _drift = 0;
            _count = 1;
            return true;
        }

        if (_count == 1)
        {
            _drift = (measurement - _offset) / dt;
            _pDd = (_pOo + r) / (dt * dt);
            _offset = measurement;
            _pOo = r;
            _count = 2;
            return true;
        }

        var offsetP = _offset + (_drift * dt);
        var pOoP = _pOo + (2 * _pOd * dt) + (_pDd * dt * dt) + (ProcessStdDev * ProcessStdDev * dt);
        var pOdP = _pOd + (_pDd * dt);
        var pDdP = _pDd + (DriftProcessStdDev * DriftProcessStdDev * dt);

        var y = measurement - offsetP;
        if (_count < MinSamples)
        {
            _count++;
        }
        else if (Math.Abs(y) > AdaptiveCutoff * maxError)
        {
            var f = ForgetFactor * ForgetFactor;
            pOoP *= f;
            pOdP *= f;
            pDdP *= f;
        }

        var s = pOoP + r;
        var kO = pOoP / s;
        var kD = pOdP / s;
        _offset = offsetP + (kO * y);
        _drift += kD * y;
        _pOo = pOoP - (kO * pOoP);
        _pOd = pOdP - (kD * pOoP);
        _pDd = pDdP - (kD * pOdP);

        _useDrift = (_drift * _drift) > (DriftSignificanceThreshold * DriftSignificanceThreshold * _pDd);
        return true;
    }

    public long ComputeServerTime(long clientTime)
    {
        var d = _useDrift ? _drift : 0;
        return clientTime + RoundAwayFromZero(_offset + (d * (clientTime - _lastUpdate)));
    }

    public long ComputeClientTime(long serverTime)
    {
        var d = _useDrift ? _drift : 0;
        return RoundAwayFromZero((serverTime - _offset + (d * _lastUpdate)) / (1 + d));
    }

    private static long RoundAwayFromZero(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
}
