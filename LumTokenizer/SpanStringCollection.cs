using System;
using System.Collections.Generic;
using System.Text;

namespace LumTokenizer
{
    public class SpanStringCollection
    {
        string origin = string.Empty;
        public string Origin => origin;

        List<Range> ranges = new List<Range>();

        public void SetOrigin(string origin)
        {
            ranges.Clear();
            this.origin = origin;
        }

        public SpanStringCollection()
        {
        }

        public void SetRanges(IList<Range> ranges)
        {
            this.ranges.Clear();
            this.ranges.AddRange(ranges);
        }
        public void Add(in Range ranges)
        {
            this.ranges.Add(ranges);
        }

        public void Clear()
        {
            this.ranges.Clear();
        }
        public int Count => ranges.Count;

        public ReadOnlySpan<char> this[int index]
        {
            get
            {
                return origin.AsSpan(ranges[index]);
            }
        }
    }
}
