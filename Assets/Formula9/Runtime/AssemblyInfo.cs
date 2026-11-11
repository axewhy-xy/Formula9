/*
 * Assembly-wide settings for Formula9.Runtime.
 *
 * Author: Choo Jian Wei
 * Date Created: 11/11/2026
 * Last Modified: 11/11/2026
 *
 * Description:
 *     Lets the EditMode tests call the runtime's internal members directly, so a
 *     rename breaks the build instead of a test that looks members up by name.
 */
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Formula9.Tests.EditMode")]
