using System.Collections.Generic;
using UnityEngine;

public class GemOptimalPathCalculator : MonoBehaviour
{
    [ContextMenu("Calculate Exact Shortest Path")]
    public void CalculateExactShortestPath()
    {
        int n = transform.childCount;
        if (n < 2)
        {
            Debug.LogWarning("Not enough gems to calculate a path.");
            return;
        }

        // Safety check: Held-Karp complexity is O(n^2 * 2^n). 
        // 20 gems takes ~1-2 seconds. >22 might freeze the editor for too long.
        if (n > 22)
        {
            Debug.LogError($"Too many gems ({n}). Exact calculation is mathematically too intensive for >22 nodes on the main thread.");
            return;
        }

        Debug.Log($"Calculating absolute shortest path for {n} gems... (this might freeze the editor for a second)");

        // 1. Gather positions and precalculate distances
        Vector2[] pos = new Vector2[n];
        Transform[] gems = new Transform[n];
        for (int i = 0; i < n; i++)
        {
            gems[i] = transform.GetChild(i);
            pos[i] = new Vector2(gems[i].position.x, gems[i].position.z);
        }

        float[,] dist = new float[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                dist[i, j] = Vector2.Distance(pos[i], pos[j]);
            }
        }

        // 2. Setup Dynamic Programming state
        int maxMask = 1 << n;
        float[,] dp = new float[maxMask, n];
        int[,] parent = new int[maxMask, n];

        for (int i = 0; i < maxMask; i++)
        {
            for (int j = 0; j < n; j++)
            {
                dp[i, j] = float.MaxValue;
                parent[i, j] = -1;
            }
        }

        // 3. Initialize starting points (any node can be a starting point)
        for (int i = 0; i < n; i++)
        {
            dp[1 << i, i] = 0f;
        }

        // 4. Run Held-Karp DP
        for (int mask = 1; mask < maxMask; mask++)
        {
            for (int i = 0; i < n; i++)
            {
                // If the i-th node is in this mask and reachable
                if ((mask & (1 << i)) != 0 && dp[mask, i] != float.MaxValue)
                {
                    for (int j = 0; j < n; j++)
                    {
                        // If the j-th node is NOT in the mask, we can transition to it
                        if ((mask & (1 << j)) == 0)
                        {
                            int nextMask = mask | (1 << j);
                            float newDist = dp[mask, i] + dist[i, j];
                            
                            if (newDist < dp[nextMask, j])
                            {
                                dp[nextMask, j] = newDist;
                                parent[nextMask, j] = i;
                            }
                        }
                    }
                }
            }
        }

        // 5. Find the minimum distance spanning all nodes
        float minDist = float.MaxValue;
        int endNode = -1;
        int finalMask = maxMask - 1;

        for (int i = 0; i < n; i++)
        {
            if (dp[finalMask, i] < minDist)
            {
                minDist = dp[finalMask, i];
                endNode = i;
            }
        }

        // 6. Reconstruct the optimal path
        List<string> pathNames = new List<string>();
        int currMask = finalMask;
        int currNode = endNode;

        while (currNode != -1)
        {
            pathNames.Add(gems[currNode].name);
            int prevNode = parent[currMask, currNode];
            currMask = currMask ^ (1 << currNode); // remove the current node from mask
            currNode = prevNode;
        }

        pathNames.Reverse(); // Path is traced backwards from the end node

        Debug.Log($"--- Optimal Shortest Path Found ---");
        Debug.Log($"Path Sequence: {string.Join(" -> ", pathNames)}");
        Debug.Log($"Total Exact Shortest Path Length (XZ only): {minDist}");
    }
}
