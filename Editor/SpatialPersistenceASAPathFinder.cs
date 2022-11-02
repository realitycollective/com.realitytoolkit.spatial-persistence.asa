// Copyright (c) Reality Collective. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using RealityCollective.ServiceFramework.Editor.Utilities;
using UnityEngine;

namespace RealityToolkit.SpatialPersistence.ASA.Editor
{
    /// <summary>
    /// Dummy scriptable object used to find the relative path of the com.xrtk.spatial-persistence.asa.
    /// </summary>
    /// <inheritdoc cref="IPathFinder" />
    public class SpatialPersistenceASAPathFinder : ScriptableObject, IPathFinder
    {
        /// <inheritdoc />
        public string Location => $"/Editor/{nameof(SpatialPersistenceASAPathFinder)}.cs";
    }
}