using JellyfinLoader.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JellyfinLoader.Utils
{
    internal class DependencyResolver
    {
        private HashSet<LocalDependencyInfo> _dependencies = [];
        private Dictionary<Guid, HashSet<InstalledPluginInfo>> _installedPlugins = [];

        public DependencyResolver() {

        }
    }
}
