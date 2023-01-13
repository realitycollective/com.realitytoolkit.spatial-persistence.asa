// Copyright (c) Reality Collective. All rights reserved.
// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace RealityToolkit.SpatialPersistence
{
    internal static class ArrayExtensions
    {
        /// <summary>
        /// A Simple array extension to turn an array of <see cref="Guid"/> in to an array of <see cref="string"/>
        /// </summary>
        /// <param name="input"></param>
        public static string[] ToStringArray(this Guid[] input)
        {
            var newArray = new string[input.Length];

            for (var i = 0; i < input.Length; i++)
            {
                newArray[i] = input[i].ToString();
            }

            return newArray;
        }
    }
}
