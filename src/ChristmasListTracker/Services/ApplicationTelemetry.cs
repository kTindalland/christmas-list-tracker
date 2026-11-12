using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ChristmasListTracker.Services;

public static class ApplicationTelemetry
{
    public const string ActivitySourceName = "ChristmasListTracker";
    public const string MeterName = "ChristmasListTracker";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("christmas_list.operation.duration", "ms");
    public static readonly Histogram<long> ImageSize =
        Meter.CreateHistogram<long>("christmas_list.image.size", "By");
    public static readonly Histogram<double> DirectUploadDuration =
        Meter.CreateHistogram<double>("christmas_list.direct_upload.duration", "ms");

    public static async Task<T> TrackAsync<T>(string operation, Func<Task<T>> action)
    {
        using var activity = ActivitySource.StartActivity(operation);
        var stopwatch = Stopwatch.StartNew();
        var outcome = "success";

        try
        {
            var result = await action();
            return result;
        }
        catch (Exception exception)
        {
            outcome = "error";
            RecordException(activity, exception);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("operation.duration_ms", stopwatch.Elapsed.TotalMilliseconds);
            OperationDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("outcome", outcome));
            activity?.SetTag("operation.outcome", outcome);
        }
    }

    public static async Task TrackAsync(string operation, Func<Task> action)
    {
        await TrackAsync(operation, async () =>
        {
            await action();
            return true;
        });
    }

    public static void RecordException(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName }
        }));
    }
}
