using System;
using System.Collections.Generic;
using System.Text;

#pragma warning disable 649

namespace smg.Serializers.JSON
{
    [Serializable]
    class JsonKeyValue<KType, VType>
    {
        public KType Key;
        public VType Value;
    }
}
