using System.Collections.Generic;
using UnityEngine;

namespace SemanticMesh.EditorTools
{
    // Ear-clipping triangulation for a simple 2D polygon. Handles convex and
    // simple concave shapes, which is all the tracer produces. Not built for
    // self-intersecting or holed polygons (out of scope per the spec).
    public static class PolygonTriangulator
    {
        // points: polygon boundary in order (CW or CCW). Returns triangle
        // indices into that same list. Empty on failure.
        public static List<int> Triangulate(IList<Vector2> points)
        {
            var indices = new List<int>();
            int n = points.Count;
            if (n < 3)
            {
                return indices;
            }

            // Work on a linked list of vertex indices. Ensure CCW winding so the
            // "is convex" test is consistent.
            var v = new List<int>(n);
            if (signed_area(points) < 0f)
            {
                for (int i = n - 1; i >= 0; i--) v.Add(i);
            }
            else
            {
                for (int i = 0; i < n; i++) v.Add(i);
            }

            int guard = 0;
            int guard_limit = n * n + 16;

            while (v.Count > 2 && guard++ < guard_limit)
            {
                bool clipped = false;
                int count = v.Count;

                for (int i = 0; i < count; i++)
                {
                    int i_prev = v[(i + count - 1) % count];
                    int i_curr = v[i];
                    int i_next = v[(i + 1) % count];

                    Vector2 a = points[i_prev];
                    Vector2 b = points[i_curr];
                    Vector2 c = points[i_next];

                    if (cross(b - a, c - b) < 0f)
                    {
                        continue; // reflex vertex, not an ear
                    }

                    // No other vertex may sit inside this candidate ear.
                    bool contains_other = false;
                    for (int j = 0; j < count; j++)
                    {
                        int idx = v[j];
                        if (idx == i_prev || idx == i_curr || idx == i_next)
                        {
                            continue;
                        }
                        if (point_in_triangle(points[idx], a, b, c))
                        {
                            contains_other = true;
                            break;
                        }
                    }

                    if (contains_other)
                    {
                        continue;
                    }

                    indices.Add(i_prev);
                    indices.Add(i_curr);
                    indices.Add(i_next);
                    v.RemoveAt(i);
                    clipped = true;
                    break;
                }

                if (!clipped)
                {
                    // Degenerate or non-simple input: bail rather than spin.
                    break;
                }
            }

            return indices;
        }

        private static float signed_area(IList<Vector2> p)
        {
            float area = 0f;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = p[i];
                Vector2 b = p[(i + 1) % n];
                area += (a.x * b.y) - (b.x * a.y);
            }
            return area * 0.5f;
        }

        private static float cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static bool point_in_triangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = cross(b - a, p - a);
            float d2 = cross(c - b, p - b);
            float d3 = cross(a - c, p - c);
            bool has_neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool has_pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(has_neg && has_pos);
        }
    }
}
