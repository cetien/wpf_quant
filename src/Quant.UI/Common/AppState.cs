using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Quant.UI.Common
{
    public static class AppState
    {
        public static string LastTicker
        {
            get => App.Config().LastTicker;
            set => App.Config().LastTicker = value;
        }

        public static string LastGroup
        {
            get => App.Config().LastGroup;
            set => App.Config().LastGroup = value;
        }
    }


}
