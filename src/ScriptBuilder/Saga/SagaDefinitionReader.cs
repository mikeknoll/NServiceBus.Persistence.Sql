﻿using System.Linq;
using Mono.Cecil;
using NServiceBus.Persistence.Sql;
using NServiceBus.Persistence.Sql.ScriptBuilder;

class SagaDefinitionReader
{

    public static bool TryGetSqlSagaDefinition(TypeDefinition type, out SagaDefinition definition)
    {
        var typeFullName = type.FullName;


        if (!type.IsAbstract && type.BaseType != null)
        {
            var baseTypeFullName = type.BaseType.FullName;
            if (baseTypeFullName.StartsWith("NServiceBus.Saga"))
            {
                throw new ErrorsException($"The type '{typeFullName}' inherits from NServiceBus.Saga which is not supported. Inherit from NServiceBus.Persistence.Sql.SqlSaga.");
            }
        }

        var attribute = type.GetSingleAttribute("NServiceBus.Persistence.Sql.SqlSagaAttribute");
        if (attribute == null)
        {
            if (!type.IsAbstract && type.BaseType != null)
            {
                var baseTypeFullName = type.BaseType.FullName;
                if (baseTypeFullName.StartsWith("NServiceBus.Persistence.Sql.SqlSaga"))
                {
                    throw new ErrorsException($"The type '{typeFullName}' inherits from NServiceBus.Persistence.Sql.SqlSaga but is missing a [SqlSagaAttribute].");
                }
            }
            definition = null;
            return false;
        }
        if (type.HasGenericParameters)
        {
            throw new ErrorsException($"The type '{typeFullName}' has a [SqlSagaAttribute] but has generic parameters.");
        }
        if (type.IsAbstract)
        {
            throw new ErrorsException($"The type '{typeFullName}' has a [SqlSagaAttribute] but has is abstract.");
        }

        var arguments = attribute.ConstructorArguments;
        var correlation = (string)arguments[0].Value;
        var transitional = (string)arguments[1].Value;
        var tableSuffix = (string)arguments[2].Value;
        SagaDefinitionValidator.ValidateSagaDefinition(correlation, typeFullName, transitional, tableSuffix);
        
        if (tableSuffix == null)
        {
            tableSuffix = type.Name;
        }

        var sagaDataType = GetSagaDataTypeFromSagaType(type);

        definition = new SagaDefinition
        (
            correlationProperty: BuildConstraintProperty(sagaDataType, correlation),
            transitionalCorrelationProperty: BuildConstraintProperty(sagaDataType, transitional),
            tableSuffix: tableSuffix,
            name: type.FullName
        );
        return true;
    }

   
    static TypeDefinition GetSagaDataTypeFromSagaType(TypeDefinition sagaType)
    {
        foreach (var method in sagaType.Methods)
        {
            var parameters = method.Parameters;
            if (method.Name == "ConfigureMapping")
            {
                if (parameters.Count == 1)
                {
                    var parameterType = parameters[0].ParameterType;
                    var parameterTypeName = parameterType.Name;
                    if (parameterTypeName.StartsWith("MessagePropertyMapper"))
                    {
                        var genericInstanceType = (GenericInstanceType)parameterType;
                        var argument = genericInstanceType.GenericArguments.Single();
                        return ToTypeDefinition(sagaType, argument);
                    }
                }
            }
        }
        throw new ErrorsException($"The type '{sagaType.FullName}' needs to override SqlSaga.ConfigureHowToFindSaga(MessagePropertyMapper).");
    }

    static TypeDefinition ToTypeDefinition(TypeDefinition sagaType, TypeReference argument)
    {
        var sagaDataType = argument as TypeDefinition;
        if (sagaDataType != null)
        {
            return sagaDataType;
        }
        throw new ErrorsException($"The type '{sagaType.FullName}' uses a SagaData type not defined in the same assembly.");
    }

    static CorrelationProperty BuildConstraintProperty(TypeDefinition sagaDataTypeDefinition, string propertyName)
    {
        if (propertyName == null)
        {
            return null;
        }
        var propertyDefinition = sagaDataTypeDefinition.Properties.SingleOrDefault(x => x.Name == propertyName);

        if (propertyDefinition != null)
        {
            return BuildConstraintProperty(propertyDefinition);
        }
        throw new ErrorsException($"Expected type '{sagaDataTypeDefinition.FullName}' to contain a property named '{propertyName}'.");

        //todo: verify not readonly
    }

    static CorrelationProperty BuildConstraintProperty(PropertyDefinition member)
    {
        var propertyType = member.PropertyType;
        return new CorrelationProperty
        (
            name: member.Name,
            type: CorrelationPropertyTypeReader.GetCorrelationPropertyType(propertyType)
        );
    }

}