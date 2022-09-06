// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Microsoft.DotNet.GenAPI.Shared;

public abstract class AssemblySymbolTraverser
{
    private readonly IAssemblySymbolOrderProvider _orderProvider;
    private readonly IAssemblySymbolFilter _filter;

    public AssemblySymbolTraverser(IAssemblySymbolOrderProvider orderProvider, IAssemblySymbolFilter filter)
    {
        _orderProvider = orderProvider;
        _filter = filter;
    }

    public void Visit(IAssemblySymbol assembly)
    {
        var namespaces = EnumerateNamespaces(assembly).Where(_filter.Include);

        foreach (var namespaceSymbol in _orderProvider.Order(namespaces))
        {
            Process(namespaceSymbol);
            Visit(namespaceSymbol);
        }
    }

    public void Visit(INamespaceSymbol namespaceSymbol)
    {
        var typeMembers = namespaceSymbol.GetTypeMembers().Where(_filter.Include);

        foreach (var typeMember in _orderProvider.Order(typeMembers))
        {
            foreach (var attribute in typeMember.GetAttributes().Where(_filter.Include))
            {
                Process(attribute);
            }

            Process(typeMember);
            Visit(typeMember);
        }
    }

    public void Visit(INamedTypeSymbol typeMember)
    {
        var members = typeMember.GetMembers().Where(_filter.Include);

        foreach (var member in _orderProvider.Order(members))
        {
            foreach (var attribute in member.GetAttributes().Where(_filter.Include))
            {
                Process(attribute);
            }

            Process(member);
        }
    }

    protected abstract void Process(INamespaceSymbol namespaceSymbol);
    protected abstract void Process(INamedTypeSymbol typeMember);
    protected abstract void Process(ISymbol member);
    protected abstract void Process(AttributeData attribute);

    private IEnumerable<INamespaceSymbol> EnumerateNamespaces(IAssemblySymbol assembly)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(assembly.GlobalNamespace);

        while (stack.TryPop(out var current))
        {
            yield return current;

            foreach (var subNamespace in current.GetNamespaceMembers())
            {
                stack.Push(subNamespace);
            }
        }
    }
}