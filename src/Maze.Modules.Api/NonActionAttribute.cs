using System;

namespace Maze.Modules.Api
{
    /// <summary>
    ///     Indicates that a controller method is not an action method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NonActionAttribute : Attribute
    {
    }
}