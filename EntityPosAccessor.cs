using System;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Common.Entities;

namespace ServerAntiCheat
{
    /// <summary>
    /// VS 1.19 has Entity.Pos as a public field. VS 1.20+ turned it into a property.
    /// Compiling against one version crashes on the other at runtime. To work around
    /// this, we probe via reflection once at startup and build a compiled Func delegate
    /// so every subsequent call is as fast as a direct member access.
    /// </summary>
    internal static class EntityPosAccessor
    {
        private static Func<Entity, EntityPos> _getPos;

        /// <summary>
        /// Call this once during StartServerSide. It figures out whether Pos is a
        /// field or property, then compiles a lambda so we never pay for reflection again.
        /// </summary>
        internal static void Init()
        {
            Type entityType = typeof(Entity);
            ParameterExpression param = Expression.Parameter(entityType, "entity");

            // Property takes priority (VS 1.20+), field is the fallback (VS 1.19)
            MemberExpression body;
            PropertyInfo prop = entityType.GetProperty("Pos",
                BindingFlags.Public | BindingFlags.Instance);

            if (prop != null && prop.PropertyType == typeof(EntityPos))
            {
                body = Expression.Property(param, prop);
            }
            else
            {
                FieldInfo field = entityType.GetField("Pos",
                    BindingFlags.Public | BindingFlags.Instance);

                if (field != null && field.FieldType == typeof(EntityPos))
                {
                    body = Expression.Field(param, field);
                }
                else
                {
                    throw new InvalidOperationException(
                        "[Shed Security] Could not find Entity.Pos as a property or field. " +
                        "This Vintage Story version may be unsupported.");
                }
            }

            _getPos = Expression.Lambda<Func<Entity, EntityPos>>(body, param).Compile();
        }

        /// <summary>
        /// The hot-path accessor. Costs one indirect call — same as a virtual method.
        /// </summary>
        internal static EntityPos GetPos(Entity entity)
        {
            return _getPos(entity);
        }
    }
}
