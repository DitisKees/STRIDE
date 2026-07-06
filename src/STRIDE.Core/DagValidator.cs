using System.Collections.Generic;
using System.Linq;
using STRIDE.Abstractions;
using STRIDE.Blocks; // Nodig voor de BlockRegistry
using STRIDE.Schema;

namespace STRIDE.Core;

public sealed class DagValidator(WorkflowConfig config)
{
    public List<string> ValidateAndDetermineOrder()
    {
        var adjacencyList = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Semantische validatie: Controleer transformatie-nodes op missende inputs
        foreach (var node in config.Nodes)
        {
            adjacencyList[node.Id] = new List<string>();
            inDegree[node.Id] = 0;

            // Instantieer het blok kortstondig om het type (Transform/Source) te achterhalen
            IBlock blockInstance;
            try
            {
                blockInstance = BlockRegistry.CreateBlock(node.Type, node.Params);
            }
            catch (KeyNotFoundException)
            {
                throw new InvalidOperationException($"Validatiefout: Node '{node.Id}' gebruikt een onbekend of niet-gecompileerd block-type '{node.Type}'.");
            }

            // Als het een ITransformBlock is, MOET er ten minste één inputpoort gedefinieerd zijn
            if (blockInstance is ITransformBlock && (node.Inputs == null || node.Inputs.Count == 0))
            {
                throw new InvalidOperationException(
                    $"Validatiefout: Node '{node.Id}' is een transformatieblok ({node.Type}) maar heeft geen 'inputs' gedefinieerd in de workflow. " +
                    "Een transformatieblok kan niet als startpunt fungeren en vereist een upstream invoerstroom.");
            }
        }

        foreach (var sink in config.Sinks)
        {
            adjacencyList[sink.Id] = new List<string>();
            inDegree[sink.Id] = 0;

            if (sink.Inputs == null || !sink.Inputs.ContainsKey("in"))
            {
                throw new InvalidOperationException($"Validatiefout: Sink '{sink.Id}' ({sink.Type}) mist de verplichte 'in' koppeling.");
            }
        }

        // 2. Koppel de edges op basis van de gedefinieerde inputs
        foreach (var node in config.Nodes)
        {
            if (node.Inputs == null) continue;
            foreach (var input in node.Inputs.Values)
            {
                string upstreamId = input.Split(':')[0];

                if (!adjacencyList.ContainsKey(upstreamId))
                {
                    throw new InvalidOperationException($"Validatiefout: Node '{node.Id}' refereert naar een niet-bestaande upstream node '{upstreamId}'.");
                }

                adjacencyList[upstreamId].Add(node.Id);
                inDegree[node.Id]++;
            }
        }

        foreach (var sink in config.Sinks)
        {
            foreach (var input in sink.Inputs.Values)
            {
                string upstreamId = input.Split(':')[0];
                if (!adjacencyList.ContainsKey(upstreamId))
                {
                    throw new InvalidOperationException($"Validatiefout: Sink '{sink.Id}' refereert naar een niet-bestaande upstream node '{upstreamId}'.");
                }

                adjacencyList[upstreamId].Add(sink.Id);
                inDegree[sink.Id]++;
            }
        }

        // 3. Kahn's Algoritme voor Topological Sort
        var queue = new Queue<string>(inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
        var order = new List<string>();

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            order.Add(current);

            foreach (string neighbor in adjacencyList[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        if (order.Count != (config.Nodes.Count + config.Sinks.Count))
        {
            throw new InvalidOperationException("Validatiefout: Cirkelreferentie (cycle) gedetecteerd in de workflow topologie. De pipeline kan niet selectief worden uitgevoerd.");
        }

        return order;
    }
}