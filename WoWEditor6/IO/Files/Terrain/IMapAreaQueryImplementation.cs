using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WoWEditor6.IO.Files.Terrain
{
    interface IMapAreaQueryImplementation
    {
        void Execute(MapAreaQuery query);
    }
}
