using System.Numerics;
using SiegeFX.Core.Nav;

namespace SiegeFX.Tests;

public class NavWaypointLayerTests
{
    // An invented L-shaped corridor of four triangles on flat ground:
    // two along X (z 0..1), then two turning north (x 2..3).
    static NavMesh LCorridor()
    {
        var v = new[]
        {
            new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 0, 1),   // t0
            new Vector3(2, 0, 0), new Vector3(2, 0, 1), new Vector3(0, 0, 1),   // t1
            new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(2, 0, 1),   // t2
            new Vector3(3, 0, 0), new Vector3(3, 0, 3), new Vector3(2, 0, 1),   // t3
        };
        var idx = Enumerable.Range(0, v.Length).ToArray();
        // Neighbour across the edge opposite each vertex.
        var n = new[]
        {
            1, -1, -1,     // t0: opposite v0 = edge (2,0,0)-(0,0,1) -> t1
            -1, 0, 2,      // t1: opposite v1 = edge (0,0,1)-(2,0,0) -> t0; opposite v2 = edge (2,0,0)-(2,0,1) -> t2
            3, 1, -1,      // t2: opposite v0 = edge (3,0,0)-(2,0,1) -> t3; opposite v1 = edge (2,0,1)-(2,0,0) -> t1
            -1, 2, -1,     // t3: opposite v1 = edge (2,0,1)-(3,0,0) -> t2
        };
        return NavMesh.FromTriangles(v, idx, n);
    }

    [Fact]
    public void WaypointsReportThePortalTheySitOn()
    {
        var mesh = LCorridor();
        var wps = new List<Vector3>();
        var portals = new List<int>();
        NavFunnel.BuildWaypoints(mesh, new[] { 0, 1, 2, 3 }, new Vector3(0.3f, 0, 0.5f), new Vector3(2.8f, 0, 2.2f), wps, portals);
        Assert.Equal(wps.Count, portals.Count);
        // The goal is portal N (the path's length); every corner sits on a
        // real portal between two path triangles.
        Assert.Equal(4, portals[^1]);
        Assert.All(portals.Take(portals.Count - 1), k => Assert.InRange(k, 1, 3));
        // The turn's inside corner (2,0,1) is a waypoint.
        Assert.Contains(wps, w => Vector3.Distance(w, new Vector3(2, 0, 1)) < 1e-4f);
    }

    [Fact]
    public void AFollowerWalksTheCorridorToItsEnd()
    {
        var mesh = LCorridor();
        var f = new NavFollower(mesh, new Vector3(0.3f, 0, 0.5f), 4f);
        f.SetTarget(new Vector3(2.8f, 0, 2.2f));
        Assert.False(f.PathBlocked);
        for (int i = 0; i < 100 && !f.ReachedGoal; i++) f.Tick(0.05f);
        Assert.True(f.ReachedGoal);
    }
}
