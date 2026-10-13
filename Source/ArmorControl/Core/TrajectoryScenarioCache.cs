using System;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Main-thread lifecycle cache. Missing modules are never cached as ready.
    internal sealed class TrajectoryScenarioCache
    {
        private object game, module;
        private int scene;

        internal object Get(object currentGame, int currentScene, Func<object> find, Func<object, bool> alive)
        {
            if (!ReferenceEquals(game, currentGame) || scene != currentScene)
            { game = currentGame; scene = currentScene; module = null; }
            if (module == null || !alive(module)) module = find();
            return module;
        }
    }
}
