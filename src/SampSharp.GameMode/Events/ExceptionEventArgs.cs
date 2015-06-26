﻿// SampSharp
// Copyright 2015 Tim Potze
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;

namespace SampSharp.GameMode.Events
{
    /// <summary>
    /// Provides data for the <see cref="BaseMode.CallbackException"/> event.
    /// </summary>
    public class ExceptionEventArgs : EventArgs
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="ExceptionEventArgs" /> class.
        /// </summary>
        /// <param name="exception">The exception.</param>
        public ExceptionEventArgs(Exception exception)
        {
            Exception = exception;
        }

        /// <summary>
        ///     Gets the exception.
        /// </summary>
        public Exception Exception { get; private set; }

        /// <summary>
        ///     Gets or sets a value indicating whether this <see cref="ExceptionEventArgs" /> is handled.
        /// </summary>
        public bool Handled { get; set; }
    }
}