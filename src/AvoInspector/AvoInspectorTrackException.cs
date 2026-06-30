using System;

namespace Avo.Inspector
{
    /// <summary>
    /// Thrown by <see cref="AvoInspector.TrackSchemaFromEvent"/> on a synchronous internal error
    /// before the event is enqueued (SPEC.md §4.2.5, §7.5). The <see cref="Exception.Message"/> is
    /// the exact rejection string mandated by the spec.
    /// </summary>
    public sealed class AvoInspectorTrackException : Exception
    {
        internal const string RejectionMessage =
            "Avo Inspector: something went wrong. Please report to support@avo.app.";

        /// <summary>Creates the exception with the spec-mandated rejection message.</summary>
        public AvoInspectorTrackException() : base(RejectionMessage)
        {
        }
    }
}
