﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Mapster.Utils;

namespace Mapster.Adapters
{
    internal class DictionaryAdapter : BaseAdapter
    {
        protected override int Score => -149;

        protected override bool CanMap(Type sourceType, Type destinationType, MapType mapType)
        {
            if (sourceType == typeof (string) || sourceType == typeof (object))
                return false;

            var dictType = destinationType.GetDictionaryType();
            return dictType?.GetGenericArguments()[0] == typeof (string);
        }

        protected override bool CanInline(Expression source, Expression destination, CompileArgument arg)
        {
            if (!base.CanInline(source, destination, arg))
                return false;

            //IgnoreNullValue isn't supported by projection
            if (arg.MapType == MapType.Projection)
                return true;
            if (arg.Settings.IgnoreNullValues == true)
                return false;
            return true;
        }

        protected override Expression CreateBlockExpression(Expression source, Expression destination, CompileArgument arg)
        {
            //### !IgnoreNullValues
            //dict.Add("Prop1", convert(src.Prop1));
            //dict.Add("Prop2", convert(src.Prop2));

            //### IgnoreNullValues
            //if (src.Prop1 != null)
            //  dict.Add("Prop1", convert(src.Prop1));
            //if (src.Prop2 != null)
            //  dict.Add("Prop2", convert(src.Prop2));

            var dictType = destination.Type.GetDictionaryType();
            var valueType = dictType.GetGenericArguments()[1];
            var indexer = dictType.GetProperties().First(item => item.GetIndexParameters().Length > 0);
            var lines = new List<Expression>();

            var dict = Expression.Variable(dictType, "dict");
            lines.Add(Expression.Assign(dict, destination));

            Func<Expression, Expression> getFn = null;
            Func<Expression, Expression, Expression> setFn = (key, value) => Expression.Assign(Expression.Property(dict, indexer, key), value);
            var strategy = arg.Settings.NameMatchingStrategy;
            if (arg.MapType == MapType.MapToTarget)
            {
                if (strategy.DestinationMemberNameConverter != NameMatchingStrategy.Identity)
                {
                    var args = dictType.GetGenericArguments();
                    var setMethod = typeof(CoreExtensions).GetMethods().First(m => m.Name == nameof(CoreExtensions.FlexibleSet))
                        .MakeGenericMethod(args[1]);
                    setFn = (key, value) => Expression.Call(setMethod, dict, key, Expression.Constant(strategy.DestinationMemberNameConverter), value);
                    var getMethod = typeof(CoreExtensions).GetMethods().First(m => m.Name == nameof(CoreExtensions.FlexibleGet))
                        .MakeGenericMethod(args[1]);
                    getFn = key => Expression.Call(getMethod, dict, key, Expression.Constant(strategy.DestinationMemberNameConverter));
                }
                else
                {
                    var args = dictType.GetGenericArguments();
                    var getMethod = typeof(CoreExtensions).GetMethods().First(m => m.Name == nameof(CoreExtensions.GetValueOrDefault))
                        .MakeGenericMethod(args);
                    getFn = key => Expression.Call(getMethod, dict, key);
                }
            }
            var properties = source.Type.GetFieldsAndProperties();
            foreach (var property in properties)
            {
                var sourceMemberName = strategy.SourceMemberNameConverter(property.Name);
                Expression key = Expression.Constant(sourceMemberName);

                var getter = property.GetExpression(source);
                var value = arg.MapType == MapType.MapToTarget
                    ? CreateAdaptToExpression(getter, getFn(key), arg)
                    : CreateAdaptExpression(getter, valueType, arg);

                var itemSet = setFn(key, value);
                if (arg.Settings.IgnoreNullValues == true && (!getter.Type.GetTypeInfo().IsValueType || getter.Type.IsNullable()))
                {
                    var condition = Expression.NotEqual(getter, Expression.Constant(null, getter.Type));
                    itemSet = Expression.IfThen(condition, itemSet);
                }
                lines.Add(itemSet);
            }

            return Expression.Block(new[] {dict}, lines);
        }

        protected override Expression CreateInlineExpression(Expression source, CompileArgument arg)
        {
            //new TDestination {
            //  { "Prop1", convert(src.Prop1) },
            //  { "Prop2", convert(src.Prop2) },
            //}

            var exp = CreateInstantiationExpression(source, arg);
            var listInit = exp as ListInitExpression;
            var newInstance = listInit?.NewExpression ?? (NewExpression)exp;

            var dictType = arg.DestinationType.GetDictionaryType();
            var valueType = dictType.GetGenericArguments()[1];
            var add = dictType.GetMethod("Add", new[] { typeof(string), valueType });
            var lines = new List<ElementInit>();
            if (listInit != null)
                lines.AddRange(listInit.Initializers);

            var nameMatching = arg.Settings.NameMatchingStrategy;
            var properties = source.Type.GetFieldsAndProperties();
            foreach (var property in properties)
            {
                var getter = property.GetExpression(source);
                var value = CreateAdaptExpression(getter, valueType, arg);

                Expression key = Expression.Constant(nameMatching.SourceMemberNameConverter(property.Name));
                var itemInit = Expression.ElementInit(add, key, value);
                lines.Add(itemInit);
            }

            return Expression.ListInit(newInstance, lines);
        }
    }
}
