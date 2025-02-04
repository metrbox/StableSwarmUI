﻿using FreneticUtilities.FreneticExtensions;
using System.Collections.Concurrent;
using System.Reflection;

namespace StableSwarmUI.DataHolders;

public interface IDataHolder
{
    [AttributeUsage(AttributeTargets.Field)]
    public class NetData : Attribute
    {
        public string Name;

        public bool Required;
    }

    public static ConcurrentDictionary<Type, DataHolderHelper> Helpers = new();

    public static DataHolderHelper GetHelper(Type t)
    {
        return Helpers.GetOrAdd(t, t2 => Activator.CreateInstance(typeof(DataHolderHelper<>).MakeGenericType(t2)) as DataHolderHelper);
    }

    IDataHolder Clone();
}

public class DataHolderHelper
{
    public Type T;

    public record struct FieldData(FieldInfo Field, IDataHolder.NetData Data)
    {
        public Type Type => Field.FieldType;
        public string Name => Data.Name;
        public bool Required => Data.Required;
    }

    public FieldData[] Fields;

    public DataHolderHelper(Type t)
    {
        T = t;
        Fields = T.GetFields().Select(f => new FieldData(f, f.GetCustomAttribute<IDataHolder.NetData>())).Where(f => f.Data is not null).ToArray();
    }
}

public class DataHolderHelper<T> : DataHolderHelper where T : IDataHolder
{
    public static readonly DataHolderHelper<T> Instance = new();

    public DataHolderHelper() : base(typeof(T))
    {
    }
}
