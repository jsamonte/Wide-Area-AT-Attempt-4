///////////////////////////////////////////////////
// Copyright(c) 2025 CodeWee. All rights reserved.
///////////////////////////////////////////////////

using System.ComponentModel;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
namespace CodeWee.GridShader
{
	[CreateAssetMenu(menuName = "CodeWee/Grid Shader Renderer Feature")]
	[DisplayName("CodeWee Grid Shader")]
	public class CodeweeGridRendererFeature : ScriptableRendererFeature
	{
		public enum QueueFilter
		{
			All,
			Opaque,
			Transparent
		}
		[System.Serializable]
		public class Settings
		{
			public bool BothSides = true;
			[HideInInspector]
			public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
			public LayerMask layerMask = ~0;
			[HideInInspector]
			public QueueFilter renderQueRange = QueueFilter.All;
			[HideInInspector]
			public string frontTag = "cwGridFront";
			[HideInInspector]
			public string backTag = "cwGridBack";
		}
		public Settings settings = new Settings();
		class DrawTagPass : ScriptableRenderPass
		{
			FilteringSettings m_Filtering;
			ShaderTagId m_ShaderTag;
			string m_ProfilerTag;
			public DrawTagPass(string profilerTag, ShaderTagId shaderTag, RenderQueueRange rqRange, LayerMask layerMask)
			{
				m_ProfilerTag = profilerTag;
				m_ShaderTag = shaderTag;
				m_Filtering = new FilteringSettings(rqRange, layerMask);
			}
			public void Setup()
			{
			}
			public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
			{
				var cmd = CommandBufferPool.Get(m_ProfilerTag);
				using (new ProfilingScope(cmd, new ProfilingSampler(m_ProfilerTag)))
				{
					context.ExecuteCommandBuffer(cmd);
					cmd.Clear();
					var drawSettings = CreateDrawingSettings(m_ShaderTag, ref renderingData, SortingCriteria.CommonTransparent);
					context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_Filtering);
				}
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
			}
		}
		DrawTagPass frontPass;
		DrawTagPass backPass;
		public override void Create()
		{
			name = "Codewee Grid Shader";
			RenderQueueRange rqRange = RenderQueueRange.all;
			switch (settings.renderQueRange)
			{
				case QueueFilter.Opaque:
					rqRange = RenderQueueRange.opaque;
					break;
				case QueueFilter.Transparent:
					rqRange = RenderQueueRange.transparent;
					break;
				case QueueFilter.All:
				default:
					rqRange = RenderQueueRange.all;
					break;
			}
			frontPass = new DrawTagPass(settings.frontTag, new ShaderTagId(settings.frontTag), rqRange, settings.BothSides ? settings.layerMask : 0);
			frontPass.renderPassEvent = settings.renderPassEvent;
			backPass = new DrawTagPass(settings.backTag, new ShaderTagId(settings.backTag), rqRange, settings.layerMask);
			backPass.renderPassEvent = settings.renderPassEvent + 1;
		}
		public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
		{
			if (frontPass != null)
			{
				frontPass.Setup();
				backPass.Setup();
				renderer.EnqueuePass(frontPass);
				renderer.EnqueuePass(backPass);
			}
		}
	}
}
