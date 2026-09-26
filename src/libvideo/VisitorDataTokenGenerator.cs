using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoLibrary
{
    internal class VisitorDataTokenGenerator : IDisposable
    {
        private bool _disposed;
        private static string _visitorData = string.Empty;

        private sealed class Candidate
        {
            public string Value { get; set; }
            public int Score { get; set; }
        }

        public static async Task<string> GetVisitorDataFromYouTube(HttpClient http)
        {
            // Return cached visitor data if available
            if (!string.IsNullOrEmpty(_visitorData))
                return _visitorData;

            if (http == null)
                throw new ArgumentNullException(nameof(http));

            try
            {
                const string url =
                    "https://www.youtube.com/sw.js_data";

                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    url))
                {
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(
                            "application/json"));

                    using (var response =
                        await http.SendAsync(request)
                            .ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();

                        string jsonString =
                            await response.Content
                                .ReadAsStringAsync()
                                .ConfigureAwait(false);

                        jsonString = RemoveXssiPrefix(jsonString);

                        using (var doc =
                            JsonDocument.Parse(jsonString))
                        {
                            string value;

                            if (!TryFindVisitorData(
                                    doc.RootElement,
                                    out value))
                            {
                                throw new Exception(
                                    "The /sw.js_data response did not " +
                                    "contain recognizable visitor data.");
                            }

                            _visitorData = value;

                            return _visitorData;
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                throw new Exception(
                    "Failed to fetch visitor data from YouTube.",
                    ex);
            }
            catch (JsonException ex)
            {
                throw new Exception(
                    "Failed to parse the YouTube /sw.js_data response.",
                    ex);
            }
        }

        private static string RemoveXssiPrefix(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            // YouTube commonly prefixes JSON responses with:
            //
            // )]}'
            //
            // followed by an optional newline.
            if (value.StartsWith(")]}'", StringComparison.Ordinal))
            {
                value = value.Substring(4);

                value = value.TrimStart(
                    '\r',
                    '\n',
                    ' ',
                    '\t');
            }

            return value;
        }

        private static bool TryFindVisitorData(
            JsonElement root,
            out string visitorData)
        {
            Candidate best = null;

            FindCandidates(
                root,
                false,
                ref best);

            // A valid protobuf candidate already scores highly.
            // Requiring a minimum avoids returning arbitrary
            // Base64-looking strings from sw.js_data.
            if (best == null || best.Score < 70)
            {
                visitorData = null;
                return false;
            }

            visitorData = best.Value;
            return true;
        }

        private static void FindCandidates(
            JsonElement element,
            bool insideContextArray,
            ref Candidate best)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    {
                        bool isContextArray =
                            LooksLikeInnertubeContextArray(element);

                        foreach (JsonElement child
                            in element.EnumerateArray())
                        {
                            if (child.ValueKind ==
                                JsonValueKind.String)
                            {
                                ConsiderCandidate(
                                    child.GetString(),
                                    insideContextArray ||
                                    isContextArray,
                                    ref best);
                            }

                            FindCandidates(
                                child,
                                insideContextArray ||
                                isContextArray,
                                ref best);
                        }

                        break;
                    }

                case JsonValueKind.Object:
                    {
                        foreach (JsonProperty property
                            in element.EnumerateObject())
                        {
                            // If YouTube ever changes sw.js_data to
                            // expose an actual named visitorData field,
                            // strongly prefer it.
                            if (string.Equals(
                                    property.Name,
                                    "visitorData",
                                    StringComparison.OrdinalIgnoreCase) &&
                                property.Value.ValueKind ==
                                    JsonValueKind.String)
                            {
                                ConsiderCandidate(
                                    property.Value.GetString(),
                                    true,
                                    ref best,
                                    100);
                            }

                            FindCandidates(
                                property.Value,
                                insideContextArray,
                                ref best);
                        }

                        break;
                    }
            }
        }

        private static bool LooksLikeInnertubeContextArray(
            JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() < 4)
            {
                return false;
            }

            JsonElement.ArrayEnumerator enumerator =
                array.EnumerateArray();

            string language = null;
            string country1 = null;
            string country2 = null;
            string address = null;

            int index = 0;

            while (enumerator.MoveNext() && index < 4)
            {
                JsonElement item = enumerator.Current;

                if (item.ValueKind != JsonValueKind.String)
                    return false;

                string value = item.GetString();

                switch (index)
                {
                    case 0:
                        language = value;
                        break;

                    case 1:
                        country1 = value;
                        break;

                    case 2:
                        country2 = value;
                        break;

                    case 3:
                        address = value;
                        break;
                }

                index++;
            }

            if (index < 4)
                return false;

            return
                LooksLikeLanguage(language) &&
                LooksLikeCountry(country1) &&
                LooksLikeCountry(country2) &&
                LooksLikeIpAddress(address);
        }

        private static bool LooksLikeLanguage(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length < 2 ||
                value.Length > 12)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!char.IsLetter(c) &&
                    c != '-' &&
                    c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool LooksLikeCountry(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length < 2 ||
                value.Length > 3)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!char.IsLetter(c))
                    return false;
            }

            return true;
        }

        private static bool LooksLikeIpAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            IPAddress address;
            return IPAddress.TryParse(value, out address);
        }

        private static void ConsiderCandidate(
            string value,
            bool insideContextArray,
            ref Candidate best,
            int additionalScore = 0)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            int score = ScoreVisitorDataCandidate(value);

            if (score < 0)
                return;

            if (insideContextArray)
                score += 40;

            score += additionalScore;

            if (best == null || score > best.Score)
            {
                best = new Candidate
                {
                    Value = value,
                    Score = score
                };
            }
        }

        private static int ScoreVisitorDataCandidate(
            string value)
        {
            // Visitor data has changed size over time.
            // Keep the bounds deliberately broad.
            if (value.Length < 16 ||
                value.Length > 8192)
            {
                return -1;
            }

            string decodedText;

            try
            {
                // sw.js_data may return padding URL-escaped
                // as %3D.
                decodedText = Uri.UnescapeDataString(value);
            }
            catch
            {
                return -1;
            }

            byte[] protobuf;

            if (!TryDecodeBase64(
                    decodedText,
                    out protobuf))
            {
                return -1;
            }

            if (protobuf.Length < 8 ||
                protobuf.Length > 8192)
            {
                return -1;
            }

            int fieldCount;
            bool beginsWithFieldOne;

            if (!LooksLikeProtobuf(
                    protobuf,
                    out fieldCount,
                    out beginsWithFieldOne))
            {
                return -1;
            }

            if (fieldCount < 2)
                return -1;

            int score = 50;

            // Known visitorData payloads normally start with
            // protobuf field 1, length-delimited.
            // This is a preference, not a hard requirement.
            if (beginsWithFieldOne)
                score += 25;

            // Historically common, but deliberately not required.
            if (decodedText.StartsWith(
                    "Cg",
                    StringComparison.Ordinal))
            {
                score += 5;
            }

            if (value.IndexOf(
                    "%3D",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                decodedText.EndsWith(
                    "=",
                    StringComparison.Ordinal))
            {
                score += 2;
            }

            return score;
        }

        private static bool TryDecodeBase64(
            string value,
            out byte[] bytes)
        {
            bytes = null;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value
                .Replace('-', '+')
                .Replace('_', '/');

            // Reject characters which clearly cannot belong to
            // Base64/Base64URL.
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];

                bool valid =
                    (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '+' ||
                    c == '/' ||
                    c == '=';

                if (!valid)
                    return false;
            }

            int remainder = normalized.Length % 4;

            if (remainder == 1)
                return false;

            if (remainder != 0)
                normalized =
                    normalized.PadRight(
                        normalized.Length + (4 - remainder),
                        '=');

            try
            {
                bytes = Convert.FromBase64String(normalized);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool LooksLikeProtobuf(
            byte[] data,
            out int fieldCount,
            out bool beginsWithFieldOne)
        {
            fieldCount = 0;
            beginsWithFieldOne = false;

            int offset = 0;
            bool firstField = true;

            while (offset < data.Length)
            {
                ulong tag;

                if (!TryReadVarint(
                        data,
                        ref offset,
                        out tag))
                {
                    return false;
                }

                if (tag == 0)
                    return false;

                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 0x07);

                if (fieldNumber <= 0)
                    return false;

                if (firstField)
                {
                    beginsWithFieldOne =
                        fieldNumber == 1 &&
                        wireType == 2;

                    firstField = false;
                }

                switch (wireType)
                {
                    // varint
                    case 0:
                        {
                            ulong ignored;

                            if (!TryReadVarint(
                                    data,
                                    ref offset,
                                    out ignored))
                            {
                                return false;
                            }

                            break;
                        }

                    // fixed64
                    case 1:
                        {
                            if (offset + 8 > data.Length)
                                return false;

                            offset += 8;
                            break;
                        }

                    // length-delimited
                    case 2:
                        {
                            ulong length;

                            if (!TryReadVarint(
                                    data,
                                    ref offset,
                                    out length))
                            {
                                return false;
                            }

                            if (length >
                                (ulong)(data.Length - offset))
                            {
                                return false;
                            }

                            offset += (int)length;
                            break;
                        }

                    // fixed32
                    case 5:
                        {
                            if (offset + 4 > data.Length)
                                return false;

                            offset += 4;
                            break;
                        }

                    // Groups are obsolete and are not expected
                    // in visitorData.
                    case 3:
                    case 4:
                    default:
                        return false;
                }

                fieldCount++;

                // Avoid accepting pathological input.
                if (fieldCount > 1024)
                    return false;
            }

            return offset == data.Length;
        }

        private static bool TryReadVarint(
            byte[] data,
            ref int offset,
            out ulong value)
        {
            value = 0;

            int shift = 0;

            for (int i = 0; i < 10; i++)
            {
                if (offset >= data.Length)
                    return false;

                byte current = data[offset++];

                value |=
                    ((ulong)(current & 0x7F)) << shift;

                if ((current & 0x80) == 0)
                    return true;

                shift += 7;
            }

            return false;
        }

        public void Dispose()
        {
            if (!_disposed)
                _disposed = true;
        }

        ~VisitorDataTokenGenerator()
        {
            Dispose();
        }
    }
}
