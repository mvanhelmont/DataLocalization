﻿using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace iQuarc.DataLocalization
{
    internal class LocalizationExpressionVisitor : ExpressionVisitor
    {
        private static readonly ConcurrentDictionary<PropertyInfo, PropertyMapping> Translations = new ConcurrentDictionary<PropertyInfo, PropertyMapping>();
        private readonly CultureInfo currentCulture;
        private bool directlyInSelectMethod;

        public LocalizationExpressionVisitor(CultureInfo currentCulture)
        {
            this.currentCulture = currentCulture ?? throw new ArgumentNullException(nameof(currentCulture));
        }

        private object CurrentLanguageCode => LocalizationConfig.CultureIdentifier(currentCulture);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            directlyInSelectMethod = false;
            if (node.Method.Name == nameof(Queryable.Select))
                directlyInSelectMethod = true;
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            directlyInSelectMethod = false;
            return Expression.New(node.Constructor, node.Arguments.Select(arg =>
            {
                var rightSide = arg as MemberExpression;
                var property = rightSide?.Member as PropertyInfo;
                if (property != null)
                {
                    PropertyMapping mapping = GetTranslationMapping(property);
                    if (mapping != null)
                    {
                        var translationExpresison = GetTranslationExpression(rightSide, mapping, CurrentLanguageCode);
                        if (translationExpresison != null)
                        {
                            return translationExpresison;
                        }
                    }
                }
                return Visit(arg);
            }));
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (directlyInSelectMethod)
            {
                PropertyMapping mapping = GetTranslationMapping(node.Member as PropertyInfo);
                if (mapping != null)
                    return GetTranslationExpression(node, mapping, CurrentLanguageCode);
                return base.VisitMember(node);
            }
            return base.VisitMember(node);
        }

        protected override MemberAssignment VisitMemberAssignment(MemberAssignment node)
        {
            directlyInSelectMethod = false;
            var rightSide = node.Expression as MemberExpression;
            var property = rightSide?.Member as PropertyInfo;
            if (property != null)
            {
                PropertyMapping mapping = GetTranslationMapping(property);
                if (mapping != null)
                {
                    var translationExpresison = GetTranslationExpression(rightSide, mapping, CurrentLanguageCode);
                    if (translationExpresison != null)
                    {
                        node = Expression.Bind(node.Member, translationExpresison);
                    }
                }
                return base.VisitMemberAssignment(node);
            }
            return base.VisitMemberAssignment(node);
        }

        private static Expression GetTranslationExpression(MemberExpression memberExpression, PropertyMapping propertyMapping, object languageCode)
        {

            // This method transforms member expression {e} to {e.Translations.Where(p => p.Language.Code == "en").Select(p => p.Property).FirstOrDefault() ?? e.Property}
            // in order to replace the e value with the localized expression

            var entity = memberExpression.Expression; // {e}

            var translationMemberReference = Expression.Property(entity, propertyMapping.TranslationsNavigationProperty); //{e.Translations}
            var typeArgs = new[]{ propertyMapping.TranslationsNavigationProperty.PropertyType.GetGenericArguments()[0] };
            var languageCodeLambda = LanguageCodeLambda(propertyMapping, languageCode);

            //{e.Translations.Where(p => p.Language.Code == "en")}
            var left = Expression.Call(typeof(Enumerable), "Where", typeArgs, translationMemberReference, languageCodeLambda);

            var param = Expression.Parameter(typeArgs[0], "p"); //{p}
            var selecTypeArguments = new[] { typeArgs[0], propertyMapping.SourceProperty.PropertyType };
            //{e.Translations.Where(p => p.Language.Code == "en").Select(p => p.Property)}
            left = Expression.Call(typeof(Enumerable), "Select", selecTypeArguments, left, Expression.Lambda(Expression.Property(param, propertyMapping.TargetProperty), param));

            //{e.Translations.Where(p => p.Language.Code == "en").Select(p => p.Property).FirstOrDefault()}
            left = Expression.Call(typeof(Enumerable), "FirstOrDefault", new[] { propertyMapping.SourceProperty.PropertyType }, left);


            // <left> ?? {e.Property} => {e.Translations.Where(p => p.Language.Code == "en").Select(p => p.Property).FirstOrDefault() ?? e.Property}
            return Expression.MakeBinary(ExpressionType.Coalesce,
                                         left,
                                         Expression.Property(entity, propertyMapping.SourceProperty));
        }

        private static LambdaExpression LanguageCodeLambda(PropertyMapping propertyMapping, object languageCode)
        {
            // Target: p => p.Language.Code == "en"
            var param = Expression.Parameter(propertyMapping.TranslationEntity, "p"); //{p}

            var languageProperty = Expression.Property(
                Expression.Property(param, propertyMapping.LanguageProperty), 
                ((MemberExpression)StripConvert(((LambdaExpression)LocalizationConfig.LanguageExpression).Body)).Member.Name); //{p.Language.Code}

            var equalsExpression = Expression.Equal(languageProperty, Expression.Constant(languageCode)); // {p.Language.Code == "en"}
            var languageCodeLambda = Expression.Lambda(equalsExpression, param); //{p => p.Language.Code == "en"}
            return languageCodeLambda;
        }

        private static Expression StripConvert(Expression expression)
        {
            if (expression.NodeType == ExpressionType.Convert)
                return ((UnaryExpression) expression).Operand;
            return expression;
        }

        private static PropertyMapping GetTranslationMapping(PropertyInfo sourceProperty)
        {
           return Translations.GetOrAdd(sourceProperty, _ =>
            {
                if (sourceProperty.PropertyType != typeof(string))
                    return null;

                var mapping = new PropertyMapping
                {
                    SourceEntity = sourceProperty.ReflectedType,
                    SourceProperty = sourceProperty
                };

                // Look for List<foo> BlaBla property (Or IList<> or whatever )  where foo has a [TranslationFor] Attribute on it
                mapping.TranslationsNavigationProperty = mapping.SourceEntity.GetProperties()
                    .FirstOrDefault(
                        p =>
                            p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments()
                            .Select(arg => arg.GetCustomAttributes<TranslationForAttribute>().FirstOrDefault())
                            .Where(atr => atr != null)
                            .Any(a => a.TranslatedEntity == mapping.SourceEntity));

                if (mapping.TranslationsNavigationProperty == null)
                    return null;

                mapping.TranslationEntity = mapping.TranslationsNavigationProperty.PropertyType.GetGenericArguments().First();
                mapping.TargetProperty = mapping.TranslationsNavigationProperty.PropertyType.GetGenericArguments().First().GetProperty(sourceProperty.Name);
                mapping.LanguageProperty = mapping.TranslationEntity.GetProperties().FirstOrDefault(p => p.PropertyType == LocalizationConfig.LocalizationType);

                if (mapping.TranslationEntity.GetProperty(sourceProperty.Name) == null 
                    || mapping.TranslationEntity.GetProperty(sourceProperty.Name).PropertyType != typeof(string)
                    || mapping.LanguageProperty == null)
                    return null;

                return mapping;
            });
        }

        private class PropertyMapping
        {
            public Type SourceEntity { get; set; }
            public Type TranslationEntity { get; set; }
            public PropertyInfo SourceProperty { get; set; }
            public PropertyInfo TranslationsNavigationProperty { get; set; }
            public PropertyInfo TargetProperty { get; set; }
            public PropertyInfo LanguageProperty { get; set; }
        }
    }
}