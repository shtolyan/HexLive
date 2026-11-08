using System;
using System.Collections.Generic;
using System.Linq;

namespace HexLive.UnityPresentation.UI
{
    // §114.8: engine-free state, shared by the native and HTML reporter.
    public sealed class BugReportQueue
    {
        public const int MaxEntries = 8;
        public const int MaxBytes = 32 * 1024 * 1024;
        public enum Delivery { Waiting, Recognizing, Sending, Failed }
        public sealed class Job
        {
            public readonly string RequestId, Context;
            public readonly byte[] Screenshot;
            public byte[] Wav;
            public string Text, Error = "", DraftId = "";
            public Delivery State;
            public int Attempts;
            public float RetryAt;
            public Job(string context, byte[] screenshot, byte[] wav, string text)
            {
                RequestId = Guid.NewGuid().ToString("D"); Context = context ?? "";
                Screenshot = screenshot ?? Array.Empty<byte>(); Wav = wav;
                Text = text?.Trim() ?? "";
            }
            public long Bytes => Screenshot.LongLength + (Wav?.LongLength ?? 0) +
                Context.Length * 2L + Text.Length * 2L;
        }
        private readonly List<Job> _jobs = new();
        public IReadOnlyList<Job> Jobs => _jobs;
        public Job Working { get; private set; }
        public int PendingCount => _jobs.Count(j => j.State != Delivery.Failed);
        public int FailedCount => _jobs.Count(j => j.State == Delivery.Failed);
        public bool HasSlot => _jobs.Count < MaxEntries && _jobs.Sum(j => j.Bytes) < MaxBytes;
        public bool Enqueue(Job job)
        {
            if (job == null || _jobs.Contains(job) || _jobs.Count >= MaxEntries ||
                job.Bytes + _jobs.Sum(j => j.Bytes) > MaxBytes) return false;
            _jobs.Add(job); return true;
        }
        public Job BeginNext(float now)
        {
            if (Working != null) return null;
            return Working = _jobs.FirstOrDefault(j => j.State == Delivery.Waiting && j.RetryAt <= now);
        }
        public void Finish(Job job)
        { _jobs.Remove(job); if (Working == job) Working = null; }
        public void Release(Job job)
        { if (Working == job) Working = null; }
        public void Fail(Job job, string error)
        {
            job.State = Delivery.Failed; job.Error = error; Release(job);
        }
        public void RetryNetwork(Job job, float now, string error)
        {
            if (job.Attempts >= 2) { Fail(job, error); return; }
            job.Error = error; job.RetryAt = now + (job.Attempts++ == 0 ? 4 : 15);
            job.State = Delivery.Waiting; Release(job);
        }
        public bool Resubmit(Job job, string text)
        {
            if (!_jobs.Contains(job) || job.State != Delivery.Failed) return false;
            job.Text = text?.Trim() ?? ""; job.Error = ""; job.Attempts = 0;
            if (job.Text.Length == 0) job.DraftId = "";
            job.RetryAt = 0; job.State = Delivery.Waiting;
            return true;
        }
        public bool Discard(Job job)
        {
            if (job == null || job == Working || job.State != Delivery.Failed) return false;
            return _jobs.Remove(job);
        }
        public static bool Retryable(long code) => code == 0 || code == 408 || code == 429 || code >= 500;
    }
}
