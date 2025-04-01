using System;
using System.Collections.Generic;

namespace DonateFly.Data
{
    public class CachedItems
    {
        public List<PlayerItem> Items { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}