using System;
using System.IO;
using System.Text;
using X2Coop.Net;

namespace X2Coop.Session
{
    /// <summary>
    /// T3.3 guest filename rule for received save streams:
    /// Saves\x2coop_guest\&lt;stem&gt;-&lt;n&gt;.json where &lt;stem&gt; comes
    /// from the decided fallback chain (T3.2 review fix):
    ///   1. SaveOffer.CampaignId when non-empty  -&gt; "campaign-&lt;id8&gt;"
    ///   2. else sanitized SaveOffer.CampaignName (the campaign's save-group
    ///      directory name, e.g. "Purple Knight" - on the wire because the
    ///      Campaign archetype carries no GuidComponent and CampaignId is
    ///      Guid.Empty on every real save)
    ///   3. else SaveOffer.OfferSessionId        -&gt; "offer-&lt;sid8&gt;"
    /// and &lt;n&gt; is a per-session incrementing counter (SaveReceiver).
    ///
    /// Deliberately game-free (no Artitas/Unity references) so the test host
    /// links it - same trick as SessionParity/DebugSettings/CoopPanelState.
    /// </summary>
    internal static class SaveFileNamer
    {
        /// <summary>Stem length cap (a campaign name is a user string - keep filenames sane).</summary>
        public const int MaxStemChars = 64;

        /// <summary>Filesystem stem for one received save stream (no extension, no dir).</summary>
        public static string Stem(Guid campaignId, string campaignName, Guid offerSessionId)
        {
            if (campaignId != Guid.Empty)
            {
                return "campaign-" + MsgCodec.ShortId(campaignId);
            }
            string clean = Sanitize(campaignName);
            if (!string.IsNullOrEmpty(clean))
            {
                return clean;
            }
            return "offer-" + MsgCodec.ShortId(offerSessionId);
        }

        /// <summary>
        /// Replace Path.GetInvalidFileNameChars with '_', trim, cap at
        /// <see cref="MaxStemChars"/>. null/empty/all-invalid -&gt; "" (the
        /// caller falls through to the next fallback).
        /// </summary>
        internal static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) { return string.Empty; }
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            }
            string clean = sb.ToString().Trim();
            if (clean.Length > MaxStemChars)
            {
                clean = clean.Substring(0, MaxStemChars).TrimEnd();
            }
            return clean;
        }
    }
}
